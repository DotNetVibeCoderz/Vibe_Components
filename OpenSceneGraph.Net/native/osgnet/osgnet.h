/*
 * osgnet — C ABI shim over OpenSceneGraph for OpenSceneGraph.Net (P/Invoke).
 * Created by Gravicode Studios, led by Kang Fadhil. MIT licensed.
 *
 * Conventions
 *  - Handles are opaque and reference counted on the C++ side; release with the matching *_release.
 *  - Matrices are 16 floats in osg::Matrixf memory order, which equals System.Numerics.Matrix4x4
 *    (row-vector convention, translation in elements 12..14).
 *  - Strings are UTF-8. Functions returning int use 0 for success / false, non-zero for true / error codes
 *    as documented.
 */
#pragma once
#include <stdint.h>

#ifdef _WIN32
#  define OSGNET_API __declspec(dllexport)
#else
#  define OSGNET_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct osgnet_node osgnet_node;
typedef struct osgnet_viewer osgnet_viewer;
typedef struct osgnet_meshes osgnet_meshes;

#define OSGNET_ABI_VERSION 1

OSGNET_API int  osgnet_abi_version(void);
OSGNET_API int  osgnet_osg_version(char* buffer, int length);
OSGNET_API void osgnet_add_library_path(const char* utf8Path);
OSGNET_API void osgnet_set_notify_level(int level); /* 0 ALWAYS .. 6 DEBUG_FP */
OSGNET_API int  osgnet_last_error(char* buffer, int length);

/* ---- nodes ---- */
OSGNET_API osgnet_node* osgnet_read_node(const char* utf8Path);
OSGNET_API int  osgnet_write_node(osgnet_node* node, const char* utf8Path);
OSGNET_API void osgnet_node_release(osgnet_node* node);
OSGNET_API void osgnet_node_bound(osgnet_node* node, float* centerRadius4);
OSGNET_API int  osgnet_node_stats(osgnet_node* node, int* nodes, int* drawables, int* vertices, int* triangles);
OSGNET_API void osgnet_optimize(osgnet_node* node);
OSGNET_API void osgnet_smooth_normals(osgnet_node* node);

OSGNET_API osgnet_node* osgnet_group_create(const char* name);
OSGNET_API osgnet_node* osgnet_transform_create(const float* matrix16);
OSGNET_API void osgnet_transform_set_matrix(osgnet_node* transform, const float* matrix16);
OSGNET_API int  osgnet_group_add_child(osgnet_node* group, osgnet_node* child);
/* primitive: 0 points, 1 lines, 2 line strip, 3 triangles, 4 triangle strip. Optional arrays may be NULL. */
OSGNET_API osgnet_node* osgnet_geometry_create(const float* positions3, const float* normals3, const float* colors4,
                                               const float* texcoords2, int vertexCount,
                                               const uint32_t* indices, int indexCount, int primitive,
                                               const float* diffuse4, int lighting);
OSGNET_API void osgnet_node_set_texture_rgba(osgnet_node* node, const uint8_t* rgba, int width, int height);

/* ---- mesh extraction (osg scene -> managed meshes) ---- */
OSGNET_API osgnet_meshes* osgnet_extract_meshes(osgnet_node* node);
OSGNET_API int  osgnet_meshes_count(osgnet_meshes* m);
OSGNET_API int  osgnet_meshes_info(osgnet_meshes* m, int index, int* vertexCount, int* indexCount, int* flags,
                                   float* diffuse4, float* worldMatrix16, int* textureWidth, int* textureHeight);
/* flags: 1 normals, 2 colors, 4 texcoords, 8 texture, 16 lighting disabled */
OSGNET_API int  osgnet_meshes_copy(osgnet_meshes* m, int index, float* positions3, float* normals3, float* colors4,
                                   float* texcoords2, uint32_t* indices, uint8_t* textureRgba);
OSGNET_API void osgnet_meshes_release(osgnet_meshes* m);

/* ---- viewer ---- */
OSGNET_API osgnet_viewer* osgnet_viewer_create_window(int x, int y, int width, int height, const char* title);
OSGNET_API osgnet_viewer* osgnet_viewer_create_offscreen(int width, int height, int samples);
OSGNET_API void osgnet_viewer_set_scene(osgnet_viewer* v, osgnet_node* scene);
OSGNET_API void osgnet_viewer_set_manipulator(osgnet_viewer* v, int kind); /* 0 trackball 1 flight 2 drive 3 terrain 4 none */
OSGNET_API void osgnet_viewer_set_view(osgnet_viewer* v, const float* view16, float fovy, float zNear, float zFar);
OSGNET_API void osgnet_viewer_set_clear_color(osgnet_viewer* v, float r, float g, float b, float a);
OSGNET_API void osgnet_viewer_set_threading(osgnet_viewer* v, int model); /* 0 single, 1 cull/draw per context, 2 draw per context, 3 cull per camera */
OSGNET_API void osgnet_viewer_add_stats(osgnet_viewer* v);
OSGNET_API void osgnet_viewer_home(osgnet_viewer* v);
OSGNET_API int  osgnet_viewer_frame(osgnet_viewer* v, double simulationTime); /* returns 1 while running, 0 when done */
OSGNET_API int  osgnet_viewer_run(osgnet_viewer* v);
/* Copies the last rendered frame as RGBA8, top row first. Returns 0 on success. Offscreen viewers only. */
OSGNET_API int  osgnet_viewer_capture(osgnet_viewer* v, uint8_t* rgba, int width, int height);
OSGNET_API void osgnet_viewer_release(osgnet_viewer* v);

#ifdef __cplusplus
}
#endif
