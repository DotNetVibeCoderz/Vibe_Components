// osgnet — C ABI shim over OpenSceneGraph 3.6 for OpenSceneGraph.Net.
// Created by Gravicode Studios, led by Kang Fadhil.
#include "osgnet.h"

#include <osg/Version>
#include <osg/Group>
#include <osg/MatrixTransform>
#include <osg/Geode>
#include <osg/Geometry>
#include <osg/Material>
#include <osg/Texture2D>
#include <osg/TriangleIndexFunctor>
#include <osg/ComputeBoundsVisitor>
#include <osg/Notify>
#include <osgDB/ReadFile>
#include <osgDB/WriteFile>
#include <osgDB/Registry>
#include <osgUtil/Optimizer>
#include <osgUtil/SmoothingVisitor>
#include <osgUtil/Statistics>
#include <osgViewer/Viewer>
#include <osgViewer/ViewerEventHandlers>
#include <osgGA/TrackballManipulator>
#include <osgGA/FlightManipulator>
#include <osgGA/DriveManipulator>
#include <osgGA/TerrainManipulator>
#include <osgGA/StateSetManipulator>

#include <cstring>
#include <mutex>
#include <string>
#include <vector>

namespace {

thread_local std::string g_lastError;

void setError(const std::string& e) { g_lastError = e; }

int copyString(const std::string& s, char* buffer, int length)
{
    if (!buffer || length <= 0) return (int)s.size() + 1;
    int n = (int)std::min<size_t>(s.size(), (size_t)length - 1);
    std::memcpy(buffer, s.data(), n);
    buffer[n] = 0;
    return (int)s.size() + 1;
}

osg::Node* N(osgnet_node* n) { return reinterpret_cast<osg::Node*>(n); }
osgnet_node* H(osg::Node* n) { if (n) n->ref(); return reinterpret_cast<osgnet_node*>(n); }

osg::Matrixf M(const float* m) { osg::Matrixf r; if (m) r.set(m); return r; }

struct ExtractedMesh
{
    std::vector<float> positions, normals, colors, texcoords;
    std::vector<uint32_t> indices;
    std::vector<uint8_t> texture;
    int texWidth = 0, texHeight = 0;
    float diffuse[4] = {0.8f, 0.8f, 0.8f, 1.f};
    float world[16];
    bool lightingOff = false;
};

struct Meshes { std::vector<ExtractedMesh> items; };

struct TriangleCollector
{
    std::vector<uint32_t>* out = nullptr;
    void operator()(unsigned int a, unsigned int b, unsigned int c)
    {
        out->push_back(a); out->push_back(b); out->push_back(c);
    }
};

bool imageToRgba(const osg::Image* img, std::vector<uint8_t>& out, int& w, int& h)
{
    if (!img || !img->data() || img->getDataType() != GL_UNSIGNED_BYTE) return false;
    w = img->s(); h = img->t();
    GLenum fmt = img->getPixelFormat();
    int comps = osg::Image::computeNumComponents(fmt);
    out.resize((size_t)w * h * 4);
    for (int y = 0; y < h; ++y)
    {
        const unsigned char* row = img->data(0, h - 1 - y); // OSG images are bottom-up
        for (int x = 0; x < w; ++x)
        {
            const unsigned char* p = row + x * comps;
            uint8_t* o = &out[((size_t)y * w + x) * 4];
            switch (fmt)
            {
            case GL_RGBA: o[0] = p[0]; o[1] = p[1]; o[2] = p[2]; o[3] = p[3]; break;
            case GL_RGB:  o[0] = p[0]; o[1] = p[1]; o[2] = p[2]; o[3] = 255; break;
            case GL_BGRA: o[0] = p[2]; o[1] = p[1]; o[2] = p[0]; o[3] = p[3]; break;
            case GL_BGR:  o[0] = p[2]; o[1] = p[1]; o[2] = p[0]; o[3] = 255; break;
            case GL_LUMINANCE: o[0] = o[1] = o[2] = p[0]; o[3] = 255; break;
            case GL_LUMINANCE_ALPHA: o[0] = o[1] = o[2] = p[0]; o[3] = p[1]; break;
            default: return false;
            }
        }
    }
    return true;
}

class MeshExtractor : public osg::NodeVisitor
{
public:
    explicit MeshExtractor(Meshes* m) : osg::NodeVisitor(TRAVERSE_ALL_CHILDREN), _meshes(m) {}

    void apply(osg::Drawable& drawable) override
    {
        osg::Geometry* geom = drawable.asGeometry();
        if (!geom) return;
        const osg::Vec3Array* verts = dynamic_cast<const osg::Vec3Array*>(geom->getVertexArray());
        if (!verts || verts->empty()) return;

        ExtractedMesh mesh;
        osg::Matrixf world = osg::computeLocalToWorld(getNodePath());
        std::memcpy(mesh.world, world.ptr(), sizeof(float) * 16);

        mesh.positions.reserve(verts->size() * 3);
        for (const auto& v : *verts) { mesh.positions.push_back(v.x()); mesh.positions.push_back(v.y()); mesh.positions.push_back(v.z()); }

        if (auto* n = dynamic_cast<const osg::Vec3Array*>(geom->getNormalArray()))
            if (geom->getNormalArray()->getBinding() == osg::Array::BIND_PER_VERTEX && n->size() == verts->size())
                for (const auto& v : *n) { mesh.normals.push_back(v.x()); mesh.normals.push_back(v.y()); mesh.normals.push_back(v.z()); }

        if (auto* c = dynamic_cast<const osg::Vec4Array*>(geom->getColorArray()))
        {
            if (geom->getColorArray()->getBinding() == osg::Array::BIND_PER_VERTEX && c->size() == verts->size())
                for (const auto& v : *c) { mesh.colors.push_back(v.r()); mesh.colors.push_back(v.g()); mesh.colors.push_back(v.b()); mesh.colors.push_back(v.a()); }
            else if (!c->empty())
                for (int k = 0; k < 4; ++k) mesh.diffuse[k] = (*c)[0][k];
        }

        if (auto* t = dynamic_cast<const osg::Vec2Array*>(geom->getTexCoordArray(0)))
            if (t->size() == verts->size())
                for (const auto& v : *t) { mesh.texcoords.push_back(v.x()); mesh.texcoords.push_back(v.y()); }

        osg::TriangleIndexFunctor<TriangleCollector> tf;
        tf.out = &mesh.indices;
        geom->accept(tf);
        if (mesh.indices.empty()) return; // lines/points are not extracted

        // walk the node path for material/texture/lighting (nearest wins)
        osg::NodePath path = getNodePath();
        path.push_back(&drawable);
        for (auto it = path.rbegin(); it != path.rend(); ++it)
        {
            const osg::StateSet* ss = (*it)->getStateSet();
            if (!ss) continue;
            if (const auto* mat = dynamic_cast<const osg::Material*>(ss->getAttribute(osg::StateAttribute::MATERIAL)))
            {
                osg::Vec4 d = mat->getDiffuse(osg::Material::FRONT);
                for (int k = 0; k < 4; ++k) mesh.diffuse[k] = d[k];
                break;
            }
        }
        for (auto it = path.rbegin(); it != path.rend(); ++it)
        {
            const osg::StateSet* ss = (*it)->getStateSet();
            if (!ss) continue;
            if (const auto* tex = dynamic_cast<const osg::Texture2D*>(ss->getTextureAttribute(0, osg::StateAttribute::TEXTURE)))
            {
                imageToRgba(tex->getImage(), mesh.texture, mesh.texWidth, mesh.texHeight);
                break;
            }
        }
        for (auto it = path.rbegin(); it != path.rend(); ++it)
        {
            const osg::StateSet* ss = (*it)->getStateSet();
            if (!ss) continue;
            osg::StateAttribute::GLModeValue mode = ss->getMode(GL_LIGHTING);
            if (mode & osg::StateAttribute::INHERIT) continue;
            mesh.lightingOff = !(mode & osg::StateAttribute::ON);
            break;
        }
        _meshes->items.push_back(std::move(mesh));
    }

private:
    Meshes* _meshes;
};

// Final draw callback copying the frame buffer into an image (for offscreen capture).
struct CaptureCallback : public osg::Camera::DrawCallback
{
    mutable std::mutex lock;
    osg::ref_ptr<osg::Image> image = new osg::Image;
    void operator()(osg::RenderInfo& ri) const override
    {
        std::lock_guard<std::mutex> g(lock);
        const osg::Viewport* vp = ri.getCurrentCamera()->getViewport();
        if (!vp) return;
        image->readPixels((int)vp->x(), (int)vp->y(), (int)vp->width(), (int)vp->height(), GL_RGBA, GL_UNSIGNED_BYTE);
    }
};

struct ViewerHolder
{
    osg::ref_ptr<osgViewer::Viewer> viewer;
    osg::ref_ptr<CaptureCallback> capture;
    bool userView = false;
    osg::Matrixd view;
};

ViewerHolder* V(osgnet_viewer* v) { return reinterpret_cast<ViewerHolder*>(v); }

} // namespace

extern "C" {

int osgnet_abi_version(void) { return OSGNET_ABI_VERSION; }

int osgnet_osg_version(char* buffer, int length) { return copyString(osgGetVersion(), buffer, length); }

void osgnet_add_library_path(const char* utf8Path)
{
    if (!utf8Path) return;
    osgDB::Registry::instance()->getLibraryFilePathList().push_front(utf8Path);
}

void osgnet_set_notify_level(int level) { osg::setNotifyLevel((osg::NotifySeverity)level); }

int osgnet_last_error(char* buffer, int length) { return copyString(g_lastError, buffer, length); }

osgnet_node* osgnet_read_node(const char* utf8Path)
{
    if (!utf8Path) { setError("null path"); return nullptr; }
    osg::ref_ptr<osg::Node> node = osgDB::readRefNodeFile(utf8Path);
    if (!node) { setError(std::string("could not read ") + utf8Path); return nullptr; }
    return H(node.get());
}

int osgnet_write_node(osgnet_node* node, const char* utf8Path)
{
    if (!node || !utf8Path) return -1;
    if (!osgDB::writeNodeFile(*N(node), utf8Path)) { setError(std::string("could not write ") + utf8Path); return -2; }
    return 0;
}

void osgnet_node_release(osgnet_node* node) { if (node) N(node)->unref(); }

void osgnet_node_bound(osgnet_node* node, float* out)
{
    if (!node || !out) return;
    const osg::BoundingSphere& bs = N(node)->getBound();
    out[0] = bs.center().x(); out[1] = bs.center().y(); out[2] = bs.center().z(); out[3] = bs.valid() ? bs.radius() : -1.f;
}

int osgnet_node_stats(osgnet_node* node, int* nodes, int* drawables, int* vertices, int* triangles)
{
    if (!node) return -1;
    osgUtil::StatsVisitor sv;
    N(node)->accept(sv);
    if (nodes) *nodes = (int)(sv._numInstancedGroup + sv._numInstancedTransform + sv._numInstancedGeode + sv._numInstancedLOD + sv._numInstancedSwitch);
    if (drawables) *drawables = (int)sv._numInstancedDrawable;
    osgUtil::Statistics& st = sv._instancedStats;
    if (vertices) *vertices = (int)st._vertexCount;
    if (triangles)
    {
        unsigned int tris = 0;
        for (const auto& p : st.getPrimitiveCountMap())
            if (p.first == GL_TRIANGLES || p.first == GL_TRIANGLE_STRIP || p.first == GL_TRIANGLE_FAN || p.first == GL_QUADS || p.first == GL_POLYGON)
                tris += p.second;
        *triangles = (int)tris;
    }
    return 0;
}

void osgnet_optimize(osgnet_node* node)
{
    if (!node) return;
    osgUtil::Optimizer opt;
    opt.optimize(N(node), osgUtil::Optimizer::DEFAULT_OPTIMIZATIONS);
}

void osgnet_smooth_normals(osgnet_node* node)
{
    if (!node) return;
    osgUtil::SmoothingVisitor sv;
    N(node)->accept(sv);
}

osgnet_node* osgnet_group_create(const char* name)
{
    osg::ref_ptr<osg::Group> g = new osg::Group;
    if (name) g->setName(name);
    return H(g.get());
}

osgnet_node* osgnet_transform_create(const float* m)
{
    osg::ref_ptr<osg::MatrixTransform> t = new osg::MatrixTransform(M(m));
    return H(t.get());
}

void osgnet_transform_set_matrix(osgnet_node* transform, const float* m)
{
    if (auto* t = dynamic_cast<osg::MatrixTransform*>(N(transform))) t->setMatrix(M(m));
}

int osgnet_group_add_child(osgnet_node* group, osgnet_node* child)
{
    auto* g = group ? N(group)->asGroup() : nullptr;
    if (!g || !child) return -1;
    return g->addChild(N(child)) ? 0 : -2;
}

osgnet_node* osgnet_geometry_create(const float* p, const float* n, const float* c, const float* t, int vc,
                                    const uint32_t* idx, int ic, int primitive, const float* diffuse, int lighting)
{
    if (!p || vc <= 0) { setError("geometry needs positions"); return nullptr; }
    osg::ref_ptr<osg::Geometry> g = new osg::Geometry;
    g->setUseVertexBufferObjects(true);
    g->setUseDisplayList(false);
    auto* va = new osg::Vec3Array(vc);
    std::memcpy(&(*va)[0], p, sizeof(float) * 3 * vc);
    g->setVertexArray(va);
    if (n)
    {
        auto* na = new osg::Vec3Array(vc);
        std::memcpy(&(*na)[0], n, sizeof(float) * 3 * vc);
        g->setNormalArray(na, osg::Array::BIND_PER_VERTEX);
    }
    if (c)
    {
        auto* ca = new osg::Vec4Array(vc);
        std::memcpy(&(*ca)[0], c, sizeof(float) * 4 * vc);
        g->setColorArray(ca, osg::Array::BIND_PER_VERTEX);
    }
    else
    {
        auto* ca = new osg::Vec4Array(1);
        (*ca)[0] = diffuse ? osg::Vec4(diffuse[0], diffuse[1], diffuse[2], diffuse[3]) : osg::Vec4(0.8f, 0.8f, 0.8f, 1.f);
        g->setColorArray(ca, osg::Array::BIND_OVERALL);
    }
    if (t)
    {
        auto* ta = new osg::Vec2Array(vc);
        std::memcpy(&(*ta)[0], t, sizeof(float) * 2 * vc);
        g->setTexCoordArray(0, ta, osg::Array::BIND_PER_VERTEX);
    }
    static const GLenum modes[] = {GL_POINTS, GL_LINES, GL_LINE_STRIP, GL_TRIANGLES, GL_TRIANGLE_STRIP};
    GLenum mode = modes[(primitive >= 0 && primitive <= 4) ? primitive : 3];
    if (idx && ic > 0)
    {
        auto* de = new osg::DrawElementsUInt(mode, ic);
        std::memcpy(&(*de)[0], idx, sizeof(uint32_t) * ic);
        g->addPrimitiveSet(de);
    }
    else g->addPrimitiveSet(new osg::DrawArrays(mode, 0, vc));

    osg::StateSet* ss = g->getOrCreateStateSet();
    auto* mat = new osg::Material;
    mat->setColorMode(osg::Material::AMBIENT_AND_DIFFUSE);
    mat->setSpecular(osg::Material::FRONT_AND_BACK, osg::Vec4(0.25f, 0.25f, 0.25f, 1.f));
    mat->setShininess(osg::Material::FRONT_AND_BACK, 32.f);
    ss->setAttributeAndModes(mat);
    if (!lighting) ss->setMode(GL_LIGHTING, osg::StateAttribute::OFF);
    float alpha = diffuse ? diffuse[3] : 1.f;
    if (alpha < 0.999f)
    {
        ss->setMode(GL_BLEND, osg::StateAttribute::ON);
        ss->setRenderingHint(osg::StateSet::TRANSPARENT_BIN);
    }
    if (!n && lighting && (mode == GL_TRIANGLES || mode == GL_TRIANGLE_STRIP))
    {
        osgUtil::SmoothingVisitor::smooth(*g);
    }
    // wrap in a Geode: several OSG writers (3ds, ...) only look for drawables under a Geode
    osg::ref_ptr<osg::Geode> geode = new osg::Geode;
    geode->addDrawable(g.get());
    return H(geode.get());
}

void osgnet_node_set_texture_rgba(osgnet_node* node, const uint8_t* rgba, int w, int h)
{
    if (!node || !rgba || w <= 0 || h <= 0) return;
    osg::ref_ptr<osg::Image> img = new osg::Image;
    img->allocateImage(w, h, 1, GL_RGBA, GL_UNSIGNED_BYTE);
    for (int y = 0; y < h; ++y) std::memcpy(img->data(0, h - 1 - y), rgba + (size_t)y * w * 4, (size_t)w * 4);
    auto* tex = new osg::Texture2D(img.get());
    tex->setWrap(osg::Texture::WRAP_S, osg::Texture::REPEAT);
    tex->setWrap(osg::Texture::WRAP_T, osg::Texture::REPEAT);
    tex->setFilter(osg::Texture::MIN_FILTER, osg::Texture::LINEAR_MIPMAP_LINEAR);
    N(node)->getOrCreateStateSet()->setTextureAttributeAndModes(0, tex);
}

osgnet_meshes* osgnet_extract_meshes(osgnet_node* node)
{
    if (!node) return nullptr;
    auto* m = new Meshes;
    MeshExtractor ex(m);
    N(node)->accept(ex);
    return reinterpret_cast<osgnet_meshes*>(m);
}

int osgnet_meshes_count(osgnet_meshes* m) { return m ? (int)reinterpret_cast<Meshes*>(m)->items.size() : 0; }

int osgnet_meshes_info(osgnet_meshes* mm, int i, int* vc, int* ic, int* flags, float* diffuse, float* world, int* tw, int* th)
{
    auto* m = reinterpret_cast<Meshes*>(mm);
    if (!m || i < 0 || i >= (int)m->items.size()) return -1;
    const ExtractedMesh& e = m->items[i];
    if (vc) *vc = (int)e.positions.size() / 3;
    if (ic) *ic = (int)e.indices.size();
    if (flags) *flags = (!e.normals.empty() ? 1 : 0) | (!e.colors.empty() ? 2 : 0) | (!e.texcoords.empty() ? 4 : 0) | (!e.texture.empty() ? 8 : 0) | (e.lightingOff ? 16 : 0);
    if (diffuse) std::memcpy(diffuse, e.diffuse, sizeof(float) * 4);
    if (world) std::memcpy(world, e.world, sizeof(float) * 16);
    if (tw) *tw = e.texWidth;
    if (th) *th = e.texHeight;
    return 0;
}

int osgnet_meshes_copy(osgnet_meshes* mm, int i, float* p, float* n, float* c, float* t, uint32_t* idx, uint8_t* tex)
{
    auto* m = reinterpret_cast<Meshes*>(mm);
    if (!m || i < 0 || i >= (int)m->items.size()) return -1;
    const ExtractedMesh& e = m->items[i];
    if (p) std::memcpy(p, e.positions.data(), e.positions.size() * sizeof(float));
    if (n && !e.normals.empty()) std::memcpy(n, e.normals.data(), e.normals.size() * sizeof(float));
    if (c && !e.colors.empty()) std::memcpy(c, e.colors.data(), e.colors.size() * sizeof(float));
    if (t && !e.texcoords.empty()) std::memcpy(t, e.texcoords.data(), e.texcoords.size() * sizeof(float));
    if (idx) std::memcpy(idx, e.indices.data(), e.indices.size() * sizeof(uint32_t));
    if (tex && !e.texture.empty()) std::memcpy(tex, e.texture.data(), e.texture.size());
    return 0;
}

void osgnet_meshes_release(osgnet_meshes* m) { delete reinterpret_cast<Meshes*>(m); }

osgnet_viewer* osgnet_viewer_create_window(int x, int y, int w, int h, const char* title)
{
    auto* holder = new ViewerHolder;
    holder->viewer = new osgViewer::Viewer;
    holder->viewer->setUpViewInWindow(x, y, w, h);
    holder->viewer->setCameraManipulator(new osgGA::TrackballManipulator);
    holder->viewer->addEventHandler(new osgGA::StateSetManipulator(holder->viewer->getCamera()->getOrCreateStateSet()));
    holder->viewer->addEventHandler(new osgViewer::WindowSizeHandler);
    holder->viewer->realize();
    if (title)
    {
        osgViewer::Viewer::Windows windows;
        holder->viewer->getWindows(windows);
        for (auto* win : windows) win->setWindowName(title);
    }
    return reinterpret_cast<osgnet_viewer*>(holder);
}

osgnet_viewer* osgnet_viewer_create_offscreen(int w, int h, int samples)
{
    osg::ref_ptr<osg::GraphicsContext::Traits> traits = new osg::GraphicsContext::Traits;
    traits->x = 0; traits->y = 0; traits->width = w; traits->height = h;
    traits->red = traits->green = traits->blue = traits->alpha = 8;
    traits->depth = 24;
    traits->doubleBuffer = false;
    traits->pbuffer = true;
    traits->samples = samples;
    traits->sampleBuffers = samples > 0 ? 1 : 0;
    osg::ref_ptr<osg::GraphicsContext> gc = osg::GraphicsContext::createGraphicsContext(traits.get());
    if (!gc.valid())
    {
        // some drivers refuse pbuffers; fall back to a hidden window
        traits->pbuffer = false;
        traits->doubleBuffer = true;
        traits->windowDecoration = false;
        gc = osg::GraphicsContext::createGraphicsContext(traits.get());
        if (!gc.valid()) { setError("could not create an offscreen OpenGL context"); return nullptr; }
    }

    auto* holder = new ViewerHolder;
    holder->viewer = new osgViewer::Viewer;
    holder->viewer->setThreadingModel(osgViewer::Viewer::SingleThreaded);
    osg::Camera* cam = holder->viewer->getCamera();
    cam->setGraphicsContext(gc.get());
    cam->setViewport(new osg::Viewport(0, 0, w, h));
    cam->setProjectionMatrixAsPerspective(45.0, (double)w / h, 0.1, 10000.0);
    GLenum buffer = traits->doubleBuffer ? GL_BACK : GL_FRONT;
    cam->setDrawBuffer(buffer);
    cam->setReadBuffer(buffer);
    holder->capture = new CaptureCallback;
    cam->setFinalDrawCallback(holder->capture.get());
    holder->viewer->setCameraManipulator(new osgGA::TrackballManipulator);
    holder->viewer->realize();
    return reinterpret_cast<osgnet_viewer*>(holder);
}

void osgnet_viewer_set_scene(osgnet_viewer* v, osgnet_node* scene)
{
    if (!v) return;
    V(v)->viewer->setSceneData(scene ? N(scene) : nullptr);
    if (V(v)->viewer->getCameraManipulator()) V(v)->viewer->home();
}

void osgnet_viewer_set_manipulator(osgnet_viewer* v, int kind)
{
    if (!v) return;
    osgGA::CameraManipulator* m = nullptr;
    switch (kind)
    {
    case 0: m = new osgGA::TrackballManipulator; break;
    case 1: m = new osgGA::FlightManipulator; break;
    case 2: m = new osgGA::DriveManipulator; break;
    case 3: m = new osgGA::TerrainManipulator; break;
    default: break;
    }
    V(v)->viewer->setCameraManipulator(m);
    V(v)->userView = (m == nullptr);
}

void osgnet_viewer_set_view(osgnet_viewer* v, const float* view16, float fovy, float zNear, float zFar)
{
    if (!v) return;
    auto* h = V(v);
    h->viewer->setCameraManipulator(nullptr);
    h->userView = true;
    h->view = osg::Matrixd(M(view16));
    osg::Camera* cam = h->viewer->getCamera();
    const osg::Viewport* vp = cam->getViewport();
    double aspect = vp ? vp->width() / vp->height() : 16.0 / 9.0;
    cam->setComputeNearFarMode(osg::CullSettings::DO_NOT_COMPUTE_NEAR_FAR);
    cam->setProjectionMatrixAsPerspective(fovy, aspect, zNear, zFar);
    cam->setViewMatrix(h->view);
}

void osgnet_viewer_set_clear_color(osgnet_viewer* v, float r, float g, float b, float a)
{
    if (v) V(v)->viewer->getCamera()->setClearColor(osg::Vec4(r, g, b, a));
}

void osgnet_viewer_set_threading(osgnet_viewer* v, int model)
{
    if (!v) return;
    static const osgViewer::ViewerBase::ThreadingModel models[] = {
        osgViewer::ViewerBase::SingleThreaded, osgViewer::ViewerBase::CullDrawThreadPerContext,
        osgViewer::ViewerBase::DrawThreadPerContext, osgViewer::ViewerBase::CullThreadPerCameraDrawThreadPerContext};
    V(v)->viewer->setThreadingModel(models[(model >= 0 && model <= 3) ? model : 0]);
}

void osgnet_viewer_add_stats(osgnet_viewer* v) { if (v) V(v)->viewer->addEventHandler(new osgViewer::StatsHandler); }

void osgnet_viewer_home(osgnet_viewer* v) { if (v && V(v)->viewer->getCameraManipulator()) V(v)->viewer->home(); }

int osgnet_viewer_frame(osgnet_viewer* v, double simulationTime)
{
    if (!v) return 0;
    auto* h = V(v);
    if (h->viewer->done()) return 0;
    if (h->userView) h->viewer->getCamera()->setViewMatrix(h->view);
    if (simulationTime < 0) h->viewer->frame();
    else h->viewer->frame(simulationTime);
    return h->viewer->done() ? 0 : 1;
}

int osgnet_viewer_run(osgnet_viewer* v) { return v ? V(v)->viewer->run() : -1; }

int osgnet_viewer_capture(osgnet_viewer* v, uint8_t* rgba, int width, int height)
{
    if (!v || !rgba) return -1;
    auto* h = V(v);
    if (!h->capture) { setError("capture requires an offscreen viewer"); return -2; }
    std::lock_guard<std::mutex> g(h->capture->lock);
    osg::Image* img = h->capture->image.get();
    if (!img->data() || img->s() != width || img->t() != height) { setError("no frame captured yet or size mismatch"); return -3; }
    for (int y = 0; y < height; ++y) std::memcpy(rgba + (size_t)y * width * 4, img->data(0, height - 1 - y), (size_t)width * 4);
    return 0;
}

void osgnet_viewer_release(osgnet_viewer* v) { delete V(v); }

} // extern "C"
