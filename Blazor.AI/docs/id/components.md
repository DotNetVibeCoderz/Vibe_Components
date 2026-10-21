# Katalog component

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/components.md)

Halaman ini berisi daftar semua Razor component di Blazor.AI, dikelompokkan per paket NuGet. Sebagian besar component AI mewarisi base class yang sama, yang menambahkan parameter `Provider`, sehingga setiap component bisa memakai model yang berbeda dari model-model yang sudah kamu daftarkan. Angka, statistik, training ML, rule, dan biologi dijalankan di .NET; model hanya diminta untuk menafsirkan, menulis, atau mengekstrak. Aplikasi galeri (`samples/Blazor.AI.Gallery`) menampilkan setiap component lengkap dengan demo langsung dan kode yang bisa kamu salin.

## Daftar isi

- [Parameter umum](#parameter-umum)
- [Blazor.AI.Components](#blazoraicomponents)
- [Blazor.AI.Visualization](#blazoraivisualization)
- [Blazor.AI.RAG](#blazorairag)
- [Blazor.AI.Agents](#blazoraiagents)
- [Blazor.AI.Decision](#blazoraidecision)
- [Blazor.AI.Analytics](#blazoraianalytics)
- [Blazor.AI.LocalModels](#blazorailocalmodels)
- [Blazor.AI.Bio](#blazoraibio)
- [Blazor.AI.ThreeD](#blazoraithreed)
- [Blazor.AI.MediaPipe](#blazoraimediapipe)

## Parameter umum

Component yang mewarisi `AiComponentBase` (namespace `Blazor.AI.Components.Infrastructure`) berbagi parameter berikut. Tabel-tabel di bawah tidak mengulanginya; setiap component menyebutkan base yang dipakainya.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Provider` | `string?` | `null` | Nama provider terdaftar yang dipakai. `null` memakai default aplikasi. |
| `Class` | `string?` | `null` | Kelas CSS tambahan untuk elemen root. |
| `OnError` | `EventCallback<Exception>` | | Dipicu saat request gagal, selain pesan error yang tampil inline. |
| *(atribut lainnya)* | `IReadOnlyDictionary<string, object>?` | `null` | Atribut yang tidak cocok ditampung di `AdditionalAttributes` dan diterapkan ke elemen root. |

Base ini juga melacak status sibuk dan error, membatalkan request sebelumnya saat request baru dimulai, dan membatasi frekuensi re-render selama token di-stream.

Tiga paket menambahkan base khusus di atas `AiComponentBase`:

| Base class | Paket | Parameter tambahan |
|---|---|---|
| `AnalyticsComponentBase` | Blazor.AI.Analytics | `Dataset` (`AnalyticsDataset?`, default `null`): pakai dataset ini, bukan dataset workspace bersama. |
| `HfComponentBase` | Blazor.AI.LocalModels | `Engine` (`InferenceEngine`, default `InferenceEngine.Local`): jalankan di CPU server ini atau di Hugging Face Inference API (`HostedApi`). `Model` (`string?`, default `null`): id model Hugging Face; setiap component kembali ke default dari `LocalModelOptions`. |
| `BioComponentBase` | Blazor.AI.Bio | `Audience` (`string`, default `"a molecular biologist"`): untuk siapa penjelasan AI ditulis. `Language` (`string?`, default `null`): bahasa penjelasan. |

Beberapa component tidak mewarisi base, tetapi mendeklarasikan `Provider` dan `Class` sendiri (tanpa `OnError`). Component yang ditandai "Tanpa panggilan AI" hanya me-render data dan tidak punya `Provider`.

---

## Blazor.AI.Components

Paket: `Blazor.AI.Components` · Namespace: `Blazor.AI.Components`

```csharp
builder.Services.AddBlazorAI(builder.Configuration).AddSimulated();
builder.Services.AddBlazorAIComponents();
```

`AddBlazorAIComponents()` mendaftarkan JS interop dan state tema yang dibutuhkan component. Panggil setelah `AddBlazorAI()`. Semua paket lain bergantung pada paket ini.

### Primitif

Blok penyusun yang dipakai oleh component lain. Kamu bisa memakainya langsung di halamanmu sendiri.

### AiCard

Kartu kaca dengan judul, ikon, aksi header opsional, body, dan footer.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string?` | `null` | Judul kartu. |
| `Subtitle` | `string?` | `null` | Baris di bawah judul. |
| `Icon` | `string?` | `null` | Kelas Bootstrap Icons, misalnya `bi-stars`. |
| `BodyClass` | `string?` | `null` | Kelas CSS tambahan untuk body. |
| `Header` | `RenderFragment?` | `null` | Menggantikan header default. |
| `Actions` | `RenderFragment?` | `null` | Tombol yang tampil di sisi kanan header. |
| `ChildContent` | `RenderFragment?` | `null` | Body kartu. |
| `Footer` | `RenderFragment?` | `null` | Footer kartu. |

Tanpa panggilan AI. Juga menerima `Class` dan atribut yang tidak cocok.

```razor
<AiCard Title="Orders" Subtitle="Last 30 days" Icon="bi-bag">
    <Actions><button class="bai-btn-ghost">Export</button></Actions>
    <ChildContent>…</ChildContent>
</AiCard>
```

### AiMarkdown

Me-render teks markdown (output model) sebagai HTML.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Text` | `string?` | `null` | Markdown yang di-render. |

Tanpa panggilan AI.

```razor
<AiMarkdown Text="@answer" />
```

### AiThinking

Indikator "sedang berpikir" yang beranimasi.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Label` | `string?` | `"Thinking…"` | Teks di samping animasi. |

```razor
@if (busy) { <AiThinking Label="Reading the document…" /> }
```

### AiError

Pesan error inline dengan tombol coba lagi opsional.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Message` | `string?` | `null` | Teks error. Tidak ada yang di-render jika kosong. |
| `OnRetry` | `EventCallback` | | Menampilkan tombol coba lagi jika diisi. |

```razor
<AiError Message="@error" OnRetry="RunAgain" />
```

### AiOutput

Panel hasil yang dipakai generator: placeholder, status sibuk, markdown yang di-render, tombol salin, dan unduh opsional.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Text` | `string?` | `null` | Teks output. |
| `IsBusy` | `bool` | `false` | Menampilkan status berpikir. |
| `Markdown` | `bool` | `true` | Render sebagai markdown, bukan teks biasa. |
| `Placeholder` | `string` | `"The result will appear here."` | Ditampilkan selama belum ada output. |
| `BusyLabel` | `string` | `"Thinking…"` | Label indikator sibuk. |
| `DownloadName` | `string?` | `null` | Jika diisi, menampilkan tombol unduh yang menyimpan teks dengan nama file ini. |
| `Actions` | `RenderFragment?` | `null` | Tombol toolbar tambahan. |

```razor
<AiOutput Text="@draft" IsBusy="@busy" DownloadName="draft.md" />
```

### AiProviderPicker

Drop-down berisi provider terdaftar dengan badge lokal, hybrid, atau cloud. Bind ke sebuah nilai, atau biarkan tanpa binding untuk mengubah default aplikasi.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Value` | `string?` | `null` | Nama provider yang dipilih. Mendukung `@bind-Value`. |
| `ValueChanged` | `EventCallback<string?>` | | Dipicu saat pilihan berubah. |
| `ShowLabel` | `bool` | `false` | Menampilkan label "Model". |

```razor
<AiProviderPicker @bind-Value="provider" ShowLabel="true" />
<AiChat Provider="@provider" />
```

### AiThemeToggle

Tombol untuk berpindah antara mode terang dan gelap, dan mengingat pilihanmu.

Parameter: hanya `Class`.

```razor
<AiThemeToggle />
```

### AiCredit

Baris kredit "Made by Gravicode Studios".

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Indonesian` | `bool` | `false` | Menampilkan kredit dalam Bahasa Indonesia. |

```razor
<AiCredit Indonesian="true" />
```

### AiTabs

Tab bersegmen.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Tabs` | `IReadOnlyList<string>` | `[]` | Label tab. Wajib. |
| `Selected` | `string?` | `null` | Tab yang dipilih. Mendukung `@bind-Selected`. |
| `SelectedChanged` | `EventCallback<string>` | | Dipicu saat tab diklik. |

```razor
<AiTabs Tabs="@(["Summary", "Details"])" @bind-Selected="tab" />
```

### AiCodeBlock

Blok kode monospace dengan tombol salin.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Code` | `string?` | `null` | Kode yang ditampilkan. |
| `Language` | `string?` | `null` | Nama bahasa, dipakai sebagai label aksesibilitas, misalnya `csharp`. |

```razor
<AiCodeBlock Code="@snippet" Language="csharp" />
```

### AiDockPanel

Panel yang bisa di-dock ke kiri atau kanan, mengambang, atau diciutkan. Dipakai oleh `AiCopilot`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Copilot"` | Judul panel. |
| `Icon` | `string?` | `"bi-stars"` | Kelas Bootstrap Icons. |
| `Mode` | `DockMode` | `DockMode.Right` | `Right`, `Left` atau `Floating`. Mendukung `@bind-Mode`. |
| `ModeChanged` | `EventCallback<DockMode>` | | Dipicu saat pengguna mengubah mode dock. |
| `Collapsed` | `bool` | `false` | Status diciutkan. Mendukung `@bind-Collapsed`. |
| `CollapsedChanged` | `EventCallback<bool>` | | Dipicu saat panel diciutkan atau dibentangkan. |
| `Width` | `int` | `400` | Lebar dalam piksel. |
| `AllowModeChange` | `bool` | `true` | Menampilkan tombol mode dock. |
| `HeaderActions` | `RenderFragment?` | `null` | Tombol header tambahan. |
| `ChildContent` | `RenderFragment?` | `null` | Body panel. |

```razor
<AiDockPanel Title="Notes" Icon="bi-journal" @bind-Mode="mode" @bind-Collapsed="collapsed">
    …
</AiDockPanel>
```

### AiScoreBars

Bar horizontal untuk probabilitas label, skor kemiripan, dan feature importance.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Values` | `IEnumerable<KeyValuePair<string, double>>` | `[]` | Pasangan label dan skor. Wajib. |
| `Highlight` | `string?` | `null` | Label yang ditonjolkan, misalnya kelas pemenang. |
| `Sort` | `bool` | `true` | Urutkan berdasarkan skor, dari yang tertinggi. |
| `AsPercent` | `bool` | `true` | Format skor sebagai persentase. |
| `Max` | `int` | `20` | Jumlah bar maksimum. |

```razor
<AiScoreBars Values="@result.Scores" Highlight="@result.Label" />
```

### Percakapan

![Conversational components](../screenshots/chat.jpg)

### AiChat

Antarmuka chat lengkap dengan balasan streaming, saran, markdown, dan tool calling opsional.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Assistant"` | Judul header. |
| `Subtitle` | `string?` | `null` | Subjudul header. |
| `Icon` | `string` | `"bi-stars"` | Ikon header. |
| `SystemPrompt` | `string?` | `null` | System prompt untuk setiap request. |
| `Placeholder` | `string` | `"Ask anything…"` | Placeholder input. |
| `AssistantName` | `string` | `"Assistant"` | Nama yang tampil di pesan asisten. |
| `UserName` | `string` | `"You"` | Nama yang tampil di pesan pengguna. |
| `EmptyTitle` | `string` | `"How can I help?"` | Judul tampilan kosong. |
| `EmptyText` | `string` | `"Ask a question or pick a starting point."` | Teks tampilan kosong. |
| `Suggestions` | `IReadOnlyList<string>?` | `null` | Prompt pembuka. |
| `ShowHeader` | `bool` | `true` | Menampilkan header. |
| `ShowHint` | `bool` | `true` | Menampilkan petunjuk keyboard di bawah input. |
| `Disabled` | `bool` | `false` | Menonaktifkan input. |
| `Height` | `string?` | `"560px"` | Tinggi CSS, misalnya `"100%"`. |
| `Tools` | `IList<AITool>?` | `null` | Tool yang boleh dipanggil model. Mengaktifkan pemanggilan fungsi otomatis. |
| `Options` | `ChatOptions?` | `null` | Opsi chat dasar (temperature, max tokens). |
| `Task` | `string` | `AiTasks.Chat` | Tag task yang dipakai oleh guardrail dan audit. |
| `ContextProvider` | `Func<Task<string?>>?` | `null` | Dipanggil sebelum setiap request untuk menambahkan konteks terbaru ke system prompt. |
| `OnResponse` | `EventCallback<string>` | | Dipicu dengan balasan lengkap asisten. |
| `OnToolCall` | `EventCallback<ToolCallInfo>` | | Dipicu untuk setiap tool call, setelah dijalankan. |
| `HeaderActions` | `RenderFragment?` | `null` | Tombol header tambahan. |
| `EmptyContent` | `RenderFragment?` | `null` | Menggantikan tampilan kosong default. |

```razor
<AiChat Title="Support" SystemPrompt="You are a support assistant."
        Tools="@tools" Suggestions="@(["Where is my order?"])" />

@code {
    IList<AITool> tools = [AIFunctionFactory.Create(LookupOrder, "lookup_order", "Order status by number")];
}
```

### AiAssistant

Tombol bantuan mengambang yang membuka chat kecil. Taruh di `MainLayout` kamu.

Mendeklarasikan `Provider` sendiri (tanpa base class).

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Assistant"` | Judul panel. |
| `Subtitle` | `string?` | `"Ask about this page"` | Subjudul panel. |
| `Provider` | `string?` | `null` | Nama provider. |
| `SystemPrompt` | `string?` | `"You are a concise, helpful assistant embedded in a web application."` | System prompt. |
| `Suggestions` | `IReadOnlyList<string>?` | `null` | Prompt pembuka. |
| `Tools` | `IList<AITool>?` | `null` | Tool yang boleh dipanggil model. |
| `ContextProvider` | `Func<Task<string?>>?` | `null` | Menambahkan konteks halaman sebelum setiap request. |
| `EmptyTitle` | `string` | `"Hi! What do you need?"` | Judul tampilan kosong. |
| `EmptyText` | `string` | `"I can explain, summarise or draft things for you."` | Teks tampilan kosong. |
| `StartOpen` | `bool` | `false` | Membuka panel saat render pertama. |

```razor
<AiAssistant Title="Help" Suggestions="@(["How do I export?"])" />
```

### AiCopilot

Copilot yang bisa di-dock (kiri, kanan, atau mengambang) dan menerima apa yang sedang tampil di layar sebagai konteks sebelum setiap pertanyaan.

Mendeklarasikan `Provider` sendiri (tanpa base class).

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Copilot"` | Judul panel. |
| `Open` | `bool` | `true` | Apakah panel terbuka. Mendukung `@bind-Open`. |
| `OpenChanged` | `EventCallback<bool>` | | Dipicu saat panel dibuka atau ditutup. |
| `Mode` | `DockMode` | `DockMode.Right` | `Right`, `Left` atau `Floating`. |
| `Width` | `int` | `400` | Lebar dalam piksel. |
| `Provider` | `string?` | `null` | Nama provider. |
| `SystemPrompt` | `string` | prompt copilot | System prompt; default-nya meminta model memakai konteks dan mendaftar perubahan yang diusulkan. |
| `Context` | `string?` | `null` | Teks yang menjelaskan apa yang sedang dilihat pengguna. Dibaca ulang sebelum setiap request. |
| `ContextProvider` | `Func<Task<string?>>?` | `null` | Alternatif `Context` untuk konteks yang harus diambil terlebih dahulu. |
| `QuickActions` | `IReadOnlyList<string>?` | `null` | Prompt sekali klik yang tampil di atas chat. |
| `Suggestions` | `IReadOnlyList<string>?` | `null` | Prompt pembuka. |
| `Tools` | `IList<AITool>?` | `null` | Tool yang boleh dipanggil model. |
| `EmptyTitle` | `string` | `"Your copilot is ready"` | Judul tampilan kosong. |
| `EmptyText` | `string` | `"It can see the record on screen. Ask it to explain, summarise or draft."` | Teks tampilan kosong. |

```razor
<AiCopilot @bind-Open="open" Context="@customer.ToSummary()"
           QuickActions="@(["Summarise this customer", "Draft a renewal email"])" />
```

### Smart component

![Smart components](../screenshots/smart.jpg)

### SmartPaste

Tombol yang mengisi form dari apa pun yang ada di clipboard. Field ditemukan dari form di sekitarnya, jadi langsung bekerja dengan markup yang sudah ada.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Label` | `string` | `"Smart Paste"` | Teks tombol. |
| `ButtonClass` | `string` | `"bai-btn bai-btn-ai"` | Kelas CSS tombol. |
| `Fields` | `IReadOnlyList<SmartPasteField>?` | `null` | Daftar field eksplisit. Jika `null`, field ditemukan dari form. |
| `ApplyToForm` | `bool` | `true` | Tulis nilai ke form di DOM. Nonaktifkan jika kamu menerapkan hasil `OnPasted` sendiri. |
| `OnPasted` | `EventCallback<SmartPasteResult>` | | Dipicu dengan nilai-nilai yang diekstrak. |

```razor
<form>
    <input name="customerName" />
    <input name="email" type="email" />
    <select name="category"><option>Billing</option><option>Shipping</option></select>
    <textarea name="issueDescription"></textarea>
    <SmartPaste />
</form>
```

### SmartTextArea

Text area yang menyarankan kelanjutan kalimat, berdasarkan peran penulis dan frasa favoritnya. Tekan Tab untuk menerima.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Value` | `string?` | `null` | Teks. Mendukung `@bind-Value`. |
| `ValueChanged` | `EventCallback<string?>` | | Dipicu saat teks berubah. |
| `Id` | `string?` | `null` | Id elemen. |
| `Rows` | `int` | `6` | Jumlah baris yang terlihat. |
| `Placeholder` | `string?` | `null` | Teks placeholder. |
| `UserRole` | `string?` | `null` | Siapa yang mengetik, misalnya "Support agent for an e-commerce store". Sangat meningkatkan kualitas saran. |
| `UserPhrases` | `IReadOnlyList<string>?` | `null` | Frasa yang sering ditulis pengguna ini; diutamakan jika relevan. |
| `Tone` | `string?` | `null` | Gaya bahasa yang diinginkan. |
| `DebounceMs` | `int` | `450` | Jeda setelah mengetik sebelum meminta saran. |

```razor
<SmartTextArea @bind-Value="reply"
               UserRole="Customer support agent"
               UserPhrases="@(["Thank you for your patience."])" />
```

### SmartComboBox

Combo box yang sarannya diurutkan berdasarkan makna, memakai embedding. Mengetik "flight to Bali" akan menyarankan "Travel & accommodation".

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Value` | `string?` | `null` | Nilai yang dipilih. Mendukung `@bind-Value`. |
| `ValueChanged` | `EventCallback<string?>` | | Dipicu saat nilai berubah. |
| `Items` | `IEnumerable<string>` | `[]` | Nilai yang diizinkan. Wajib. |
| `Id` | `string?` | `null` | Id elemen. |
| `Placeholder` | `string?` | `null` | Teks placeholder. |
| `MaxSuggestions` | `int` | `6` | Jumlah saran maksimum yang ditampilkan. |
| `MinScore` | `double` | `0.05` | Menyembunyikan saran dengan cosine similarity di bawah nilai ini. |
| `ShowScores` | `bool` | `true` | Menampilkan nilai kemiripan di samping setiap saran. |
| `DebounceMs` | `int` | `250` | Jeda setelah mengetik sebelum mencari. |

```razor
<SmartComboBox @bind-Value="category" Items="@categories" />
```

### SmartSemanticSearch\<TItem\>

Mencari di koleksi apa pun berdasarkan makna dan me-render hasilnya dengan template milikmu sendiri.

Base: `AiComponentBase`. Generic: `TItem`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Items` | `IEnumerable<TItem>` | `[]` | Item yang dicari. Wajib. |
| `TextSelector` | `Func<TItem, string>` | `ToString()` | Teks yang di-embed untuk setiap item (judul plus deskripsi hasilnya bagus). Wajib. |
| `ItemTemplate` | `RenderFragment<TItem>?` | `null` | Cara me-render sebuah hasil. |
| `Placeholder` | `string` | `"Describe what you are looking for…"` | Placeholder input. |
| `Top` | `int` | `8` | Jumlah hasil maksimum. |
| `MinScore` | `double` | `0.08` | Kemiripan minimum. |
| `InitialQuery` | `string?` | `null` | Query yang dijalankan saat render pertama. |
| `OnResults` | `EventCallback<IReadOnlyList<SemanticMatch<TItem>>>` | | Dipicu dengan hasil yang sudah diurutkan. |

```razor
<SmartSemanticSearch TItem="Product" Items="@products" TextSelector="@(p => p.Name + ". " + p.Description)">
    <ItemTemplate>@context.Name</ItemTemplate>
</SmartSemanticSearch>
```

### SmartRecommendations\<TItem\>

Menampilkan item yang mirip dengan yang disukai, dilihat, atau dibeli pengguna, masing-masing dengan alasan satu baris opsional.

Base: `AiComponentBase`. Generic: `TItem`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Recommended for you"` | Judul kartu. |
| `Subtitle` | `string?` | `"Based on what you viewed"` | Subjudul kartu. |
| `Items` | `IEnumerable<TItem>` | `[]` | Item kandidat. Wajib. |
| `Seeds` | `IEnumerable<TItem>` | `[]` | Item yang pernah diinteraksikan pengguna. Item ini dikecualikan dari hasil. Wajib. |
| `TextSelector` | `Func<TItem, string>` | `ToString()` | Teks yang di-embed untuk setiap item. Wajib. |
| `ItemTemplate` | `RenderFragment<TItem>?` | `null` | Cara me-render sebuah rekomendasi. |
| `Count` | `int` | `4` | Jumlah rekomendasi. |
| `ExplainWithAI` | `bool` | `true` | Minta model menjelaskan setiap rekomendasi dalam satu baris. |

```razor
<SmartRecommendations TItem="Product" Items="@products" Seeds="@viewed" TextSelector="@(p => p.Description)" />
```

### SmartSummarizer

Merangkum teks panjang menjadi paragraf, poin-poin, TL;DR, atau ringkasan eksekutif, dalam bahasa apa pun.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Summariser"` | Judul kartu. |
| `Subtitle` | `string?` | `"Long text in, key points out"` | Subjudul kartu. |
| `Text` | `string?` | `null` | Teks yang dirangkum. |
| `ShowInput` | `bool` | `true` | Menampilkan kotak input. Sembunyikan untuk hanya merangkum `Text`. |
| `Style` | `SummaryStyle` | `SummaryStyle.Bullets` | `Paragraph`, `Bullets`, `TlDr` atau `Executive`. |
| `MaxWords` | `int` | `120` | Target panjang. |
| `Language` | `string?` | `null` | Bahasa output. |
| `OnSummarized` | `EventCallback<string>` | | Dipicu dengan ringkasan. |

```razor
<SmartSummarizer Text="@longText" Style="SummaryStyle.Bullets" MaxWords="120" />
```

### SmartTranslator

Menerjemahkan teks dengan tetap menjaga gaya bahasa dan format, termasuk bahasa daerah seperti Jawa dan Sunda.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Translator"` | Judul kartu. |
| `Subtitle` | `string?` | `"Keeps tone and formatting"` | Subjudul kartu. |
| `Text` | `string?` | `null` | Teks yang diterjemahkan. |
| `Source` | `string?` | `null` | Bahasa sumber. `null` akan mendeteksinya otomatis. |
| `Target` | `string` | `"Bahasa Indonesia"` | Bahasa tujuan. |
| `Languages` | `IReadOnlyList<string>` | 17 bahasa | Bahasa yang ditawarkan di picker. |
| `OnTranslated` | `EventCallback<string>` | | Dipicu dengan hasil terjemahan. |

```razor
<SmartTranslator Text="@text" Target="Bahasa Indonesia" />
```

### SmartClassifier

Klasifikasi zero-shot teks ke dalam label milikmu sendiri, dengan tingkat keyakinan per label.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Classifier"` | Judul kartu. |
| `Subtitle` | `string?` | `"Sort text into your own categories"` | Subjudul kartu. |
| `Text` | `string?` | `null` | Teks yang diklasifikasi. |
| `Labels` | `IReadOnlyList<string>` | `["Positive", "Negative", "Neutral"]` | Kategori. |
| `EditableLabels` | `bool` | `true` | Mengizinkan pengguna mengubah label. |
| `ShowInput` | `bool` | `true` | Menampilkan kotak input. |
| `Instructions` | `string?` | `null` | Panduan tambahan, misalnya "Classify by the customer's main intent". |
| `Placeholder` | `string` | `"Paste a review, ticket, email or message…"` | Placeholder input. |
| `OnClassified` | `EventCallback<ClassificationResult>` | | Dipicu dengan label, tingkat keyakinan, dan skor per label. |

```razor
<SmartClassifier Text="@message" Labels="@(["Billing", "Shipping", "Technical"])" OnClassified="Route" />
```

### SmartEmailComposer

Mengubah poin-poin menjadi email siap kirim, lalu menawarkan penyempurnaan sekali klik.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Email composer"` | Judul kartu. |
| `Subtitle` | `string?` | `"From bullet points to a ready-to-send email"` | Subjudul kartu. |
| `OnDrafted` | `EventCallback<string>` | | Dipicu dengan draf email. |

```razor
<SmartEmailComposer OnDrafted="SaveDraft" />
```

### Generator

![Content generators](../screenshots/content.jpg)

Semua generator dikendalikan oleh sebuah `GeneratorSpec`: form deklaratif (daftar `GeneratorField`), system prompt, template dengan placeholder `{{field}}`, temperature, dan penyempurnaan lanjutan. Generator siap pakai membungkus `AiGenerator` dengan spec dari `GeneratorSpecs`.

### AiGenerator

Generator dari form ke output untuk `GeneratorSpec` apa pun yang kamu definisikan.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Spec` | `GeneratorSpec` | | Form, prompt, dan pengaturan output. Wajib. |
| `Values` | `IReadOnlyDictionary<string, string>?` | `null` | Nilai awal form, dengan key nama field. |
| `OnGenerated` | `EventCallback<string>` | | Dipicu dengan teks yang dihasilkan. |

```razor
<AiGenerator Spec="@releaseNotes" />

@code {
    GeneratorSpec releaseNotes = new()
    {
        Title = "Release notes",
        SystemPrompt = "You write concise release notes for developers.",
        Template = "Write release notes for version {{version}} from these changes:\n{{changes}}",
        Fields =
        [
            new("version", "Version", Required: true),
            new("changes", "Changes", GeneratorFieldKind.TextArea, Wide: true),
        ],
    };
}
```

### ContentGenerator, BlogWriter, StoryGenerator, MarketingGenerator, SocialMediaGenerator

Generator siap pakai untuk konten pemasaran dan editorial.

| Component | Yang ditulis | `Spec` default |
|---|---|---|
| `ContentGenerator` | Artikel, FAQ, siaran pers, copy produk, dan lainnya. | `GeneratorSpecs.Content` |
| `BlogWriter` | Post terstruktur yang ramah SEO, lengkap dengan meta description dan keyword. | `GeneratorSpecs.Blog` |
| `StoryGenerator` | Cerita pendek dengan genre, latar, tokoh, dan usia pembaca. | `GeneratorSpecs.Story` |
| `MarketingGenerator` | Value proposition, headline, iklan, subjek email, dan CTA sekaligus. | `GeneratorSpecs.Marketing` |
| `SocialMediaGenerator` | Post yang sesuai gaya tiap platform dalam beberapa varian. | `GeneratorSpecs.SocialMedia` |

Semuanya menerima parameter yang sama dan mendeklarasikan `Provider` serta `Class` sendiri:

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Spec` | `GeneratorSpec` | lihat tabel di atas | Definisi generator. Override untuk mengubah prompt atau field. |
| `Provider` | `string?` | `null` | Nama provider. |
| `Values` | `IReadOnlyDictionary<string, string>?` | `null` | Nilai awal form, dengan key nama field. |
| `OnGenerated` | `EventCallback<string>` | | Dipicu dengan teks yang dihasilkan. |

```razor
<ContentGenerator Values="@(new Dictionary<string, string> { ["topic"] = "..." })" OnGenerated="Save" />
<BlogWriter Provider="anthropic" />
<StoryGenerator Spec="@(GeneratorSpecs.Story with { Temperature = 1.0f })" />
<MarketingGenerator />
<SocialMediaGenerator OnGenerated="Schedule" />
```

### Coding

![Coding components](../screenshots/coding.jpg)

### CodeAssistant

Pair programmer yang paham bahasa dan framework, dalam tata letak chat.

Mendeklarasikan `Provider` sendiri (tanpa base class).

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Code assistant"` | Judul header. |
| `Provider` | `string?` | `null` | Nama provider. |
| `Height` | `string?` | `"600px"` | Tinggi CSS. |
| `Languages` | `IReadOnlyList<string>` | `["C#", "Razor", "TypeScript", "JavaScript", "Python", "SQL", "Go", "Rust", "Java", "Kotlin", "Bash"]` | Bahasa yang ditawarkan di picker. |
| `Suggestions` | `IReadOnlyList<string>` | prompt pembuka seputar coding | Prompt pembuka. |

```razor
<CodeAssistant Languages="@(["C#", "TypeScript"])" />
```

### CodeReviewPanel

Me-review kode dan mengembalikan skor plus temuan yang diurutkan berdasarkan tingkat keparahan (bug, keamanan, performa), lengkap dengan nomor baris dan perbaikannya.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Code review"` | Judul kartu. |
| `Subtitle` | `string?` | `"Bugs, security and performance, ranked"` | Subjudul kartu. |
| `Code` | `string?` | `null` | Kode atau diff yang di-review. |
| `OnReviewed` | `EventCallback<ReviewResult>` | | Dipicu dengan hasil review. |

```razor
<CodeReviewPanel Code="@diff" OnReviewed="PostToPullRequest" />
```

### ArchitectureAdvisor

Generator yang merekomendasikan arsitektur beserta trade-off, risiko, milestone, dan diagram mermaid.

Parameternya sama dengan generator konten (`Spec`, `Provider`, `Class`, `Values`, `OnGenerated`). `Spec` default: `GeneratorSpecs.Architecture`.

```razor
<ArchitectureAdvisor />
```

### DocumentationGenerator

Generator yang menulis bagian README, referensi API, atau XML doc comment dari kode.

Parameternya sama dengan generator konten. `Spec` default: `GeneratorSpecs.Documentation`.

```razor
<DocumentationGenerator Values="@(new Dictionary<string, string> { ["code"] = source })" />
```

### Produktivitas

![Productivity components](../screenshots/productivity.jpg)

### PromptBuilder

Menyusun prompt dari peran, task, konteks, batasan, dan contoh, lalu memungkinkan kamu menguji, menyempurnakan, dan menyimpannya.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Prompt builder"` | Judul kartu. |
| `Subtitle` | `string?` | `"Structure a prompt, test it, save it"` | Subjudul kartu. |
| `Category` | `string` | `"Custom"` | Kategori untuk prompt yang disimpan. |
| `OnSaved` | `EventCallback<PromptTemplate>` | | Dipicu saat prompt disimpan. |

```razor
<PromptBuilder Category="Support" OnSaved="Notify" />
```

### PromptLibrary

Cari, tandai favorit, dan jalankan prompt yang tersimpan. Variabel template berubah menjadi form. Prompt disimpan melalui `IPromptStore` (terenkripsi AES-GCM).

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Prompt library"` | Judul kartu. |
| `OnRun` | `EventCallback<string>` | | Dipicu dengan output dari sebuah run. |

```razor
<PromptLibrary />
```

### JsonSchemaGenerator

Menyimpulkan JSON Schema dari contoh JSON (secara lokal, tanpa AI) atau dari deskripsi dalam kata-kata, sekaligus menghasilkan record C# dan type TypeScript.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"JSON Schema generator"` | Judul kartu. |
| `Subtitle` | `string?` | `"Schema, C# records and TypeScript types"` | Subjudul kartu. |
| `OnGenerated` | `EventCallback<JsonObject>` | | Dipicu dengan schema yang dihasilkan. |

```razor
<JsonSchemaGenerator OnGenerated="UseSchema" />
```

### StructuredOutputForm\<TModel\>

Mengekstrak objek C# bertipe kuat dari teks bebas. JSON Schema-nya diambil dari type tersebut.

Base: `AiComponentBase`. Generic: `TModel` (`class, new()`).

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Model` | `TModel` | `new()` | Objek hasil ekstraksi. Mendukung `@bind-Model`. |
| `ModelChanged` | `EventCallback<TModel>` | | Dipicu setelah ekstraksi. |
| `Title` | `string` | `"Structured output"` | Judul kartu. |
| `SourceLabel` | `string` | `"Source text"` | Label input. |
| `SourceText` | `string?` | `null` | Teks sumber ekstraksi. |
| `Placeholder` | `string` | `"Paste an email, document or note to extract from…"` | Placeholder input. |
| `Instructions` | `string?` | `null` | Panduan tambahan untuk ekstraksi. |

```razor
<StructuredOutputForm TModel="Invoice" @bind-Model="invoice" SourceText="@pastedText" />

@code {
    Invoice invoice = new();

    public sealed class Invoice
    {
        public string? InvoiceNumber { get; set; }
        public decimal? Total { get; set; }
        public DateOnly? IssueDate { get; set; }
    }
}
```

### WorkflowAssistant

Mengubah sebuah tujuan menjadi langkah-langkah yang bisa diedit, lalu menjalankannya berurutan; output setiap langkah menjadi input langkah berikutnya.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Workflow assistant"` | Judul kartu. |
| `Subtitle` | `string?` | `"Plan, edit, then execute step by step"` | Subjudul kartu. |
| `MaxSteps` | `int` | `6` | Jumlah langkah maksimum dalam rencana. |
| `OnCompleted` | `EventCallback<IReadOnlyList<WorkflowStep>>` | | Dipicu dengan semua langkah dan output-nya. |

```razor
<WorkflowAssistant MaxSteps="6" OnCompleted="Archive" />
```

---

## Blazor.AI.Visualization

Paket: `Blazor.AI.Visualization` · Namespace: `Blazor.AI.Visualization`

Tidak perlu registrasi tambahan. Chart di-render sebagai SVG di .NET; `AiDashboard` membutuhkan setup `AddBlazorAI()` dan `AddBlazorAIComponents()` di atas untuk fitur insight-nya.

```razor
@using Blazor.AI.Visualization
```

![Visual analytics](../screenshots/visualization.jpg)

### AiDashboard

Dashboard dengan tile KPI (lengkap dengan sparkline), grid 12 kolom untuk chart kamu, dan insight AI yang ditulis dari angka-angkanya.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Dashboard"` | Judul dashboard. |
| `Subtitle` | `string?` | `null` | Subjudul dashboard. |
| `Metrics` | `IReadOnlyList<DashboardMetric>` | `[]` | Tile KPI (`Label`, `Value`, `Format`, `Change` dalam persen, `Icon`, serta `Trend` dan `LowerIsBetter` opsional). |
| `DataSummary` | `string?` | `null` | Fakta tambahan untuk prompt insight, berupa teks biasa. |
| `EnableInsights` | `bool` | `true` | Menampilkan panel insight AI. |
| `Audience` | `string?` | `"a business manager"` | Untuk siapa insight ditulis. |
| `HeaderContent` | `RenderFragment?` | `null` | Konten header tambahan. |
| `ChildContent` | `RenderFragment?` | `null` | Panel-panel. Bungkus masing-masing dalam `div` dengan `bai-span-4`, `-6`, `-8` atau `-12`. |

```razor
<AiDashboard Title="Q3" Metrics="@metrics" DataSummary="@extraFacts">
    <div class="bai-span-8"><LineChart Series="@series" /></div>
    <div class="bai-span-4"><BarChart Values="@regions" Horizontal="true" /></div>
</AiDashboard>

@code {
    DashboardMetric[] metrics =
    [
        new("Revenue", 1_250_000, "C0", Change: 12) { Trend = [9, 10, 12, 11, 13] },
        new("Churn", 0.041, "P1", Change: -0.4) { LowerIsBetter = true },
    ];
}
```

### LineChart

Line chart dengan banyak series, confidence band, titik yang ditandai, dan marker vertikal.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Series` | `IReadOnlyList<ChartSeries>` | `[]` | Series yang digambar. Wajib. |
| `Bands` | `IReadOnlyList<ChartBand>?` | `null` | Band berarsir (confidence interval). |
| `Highlights` | `IReadOnlyList<ChartPoint>?` | `null` | Titik yang ditandai. |
| `Title` | `string?` | `null` | Judul chart. |
| `XLabel` / `YLabel` | `string?` | `null` | Label sumbu. |
| `Width` / `Height` | `int` | `640` / `280` | Ukuran view SVG. |
| `ShowLegend` | `bool` | `true` | Menampilkan legend. |
| `ShowMarkers` | `bool` | `false` | Menggambar titik di setiap data point. |
| `ZeroBased` | `bool` | `false` | Memulai sumbu y dari nol. |
| `YMin` / `YMax` | `double?` | `null` | Rentang y tetap. |
| `MarkerX` / `MarkerLabel` | `double?` / `string?` | `null` | Garis marker vertikal, misalnya "now". |
| `XFormatter` / `YFormatter` | `Func<double, string>?` | `null` | Formatter label tick. |

```razor
<LineChart Series="@([ChartSeries.FromValues("Sales", sales), ChartSeries.FromValues("Target", target) with { Dashed = true }])"
           YLabel="Units" ShowMarkers="true" />
```

### BarChart

Bar chart vertikal atau horizontal, opsional dikelompokkan atau diwarnai berdasarkan tanda positif/negatif.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Values` | `IEnumerable<BarValue>` | `[]` | Bar (`Category`, `Value`, `Group` opsional). Wajib. |
| `Title` | `string?` | `null` | Judul chart. |
| `Horizontal` | `bool` | `false` | Menggambar bar horizontal. |
| `ShowValues` | `bool` | `true` | Menampilkan nilai di setiap bar. |
| `Width` / `Height` | `int` | `640` / `260` | Ukuran view SVG. |
| `ColorBySign` | `bool` | `false` | Memberi warna berbeda untuk bar positif dan negatif; berguna untuk koefisien dan delta. |

```razor
<BarChart Values="@([new BarValue("Java", 420), new BarValue("Bali", 310)])" Horizontal="true" />
```

### ScatterChart

Scatter plot dengan garis identitas dan garis tren opsional.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Series` | `IReadOnlyList<ChartSeries>` | `[]` | Series titik. Wajib. |
| `Title` | `string?` | `null` | Judul chart. |
| `XLabel` / `YLabel` | `string?` | `null` | Label sumbu. |
| `IdentityLine` | `bool` | `false` | Menggambar y = x. |
| `TrendLine` | `bool` | `false` | Menggambar garis tren hasil fitting. |
| `PointRadius` | `double` | `3.5` | Ukuran titik. |
| `Width` / `Height` | `int` | `640` / `300` | Ukuran view SVG. |

```razor
<ScatterChart Series="@([new ChartSeries("Houses", points)])" XLabel="m²" YLabel="Price" TrendLine="true" />
```

### Sparkline

Garis tren mungil inline untuk tabel dan tile KPI.

Tanpa panggilan AI. Tanpa parameter `Class`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Values` | `IReadOnlyList<double>` | `[]` | Nilai-nilai. Wajib. |
| `Width` / `Height` | `int` | `96` / `28` | Ukuran dalam piksel. |
| `Color` | `string` | `"var(--bai-soga)"` | Warna garis. |

```razor
<Sparkline Values="@last30Days" />
```

### ChartLegend

Baris legend berisi kotak-kotak warna, dipakai oleh chart dan bisa dipakai ulang di layout kamu sendiri.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Items` | `IReadOnlyList<(string Name, string Color)>` | `[]` | Entri legend. Wajib. |

```razor
<ChartLegend Items="@([("Actual", "var(--bai-soga)"), ("Forecast", "var(--bai-nila)")])" />
```

### Heatmap

Me-render matriks apa pun dengan colormap Visual.Net.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Values` | `double[,]?` | | Matriks yang digambar. Wajib. |
| `RowLabels` / `ColumnLabels` | `IReadOnlyList<string>?` | `null` | Label sumbu. |
| `Title` | `string?` | `null` | Judul chart. |
| `Colormap` | `string` | `"viridis"` | Nama colormap Visual.Net, misalnya `magma`, `coolwarm`, `RdBu`. |
| `ShowValues` | `bool` | `true` | Menampilkan nilai di dalam sel. |
| `Min` / `Max` | `double?` | `null` | Rentang warna tetap. |
| `Width` | `int` | `640` | Lebar view SVG. |
| `LabelWidth` | `int` | `110` | Ruang untuk label baris. |
| `TopLabelHeight` | `int` | `70` | Ruang untuk label kolom. |

```razor
<Heatmap Values="@matrix" RowLabels="@days" ColumnLabels="@hours" Colormap="magma" />
```

### SankeyDiagram

Menampilkan aliran antar tahap, misalnya dari mana pengunjung datang dan di mana mereka berhenti.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Links` | `IReadOnlyList<SankeyLink>` | `[]` | Aliran (`Source`, `Target`, `Value`). Wajib. |
| `Title` | `string?` | `null` | Judul chart. |
| `Width` / `Height` | `int` | `760` / `360` | Ukuran view SVG. |
| `NodeWidth` | `int` | `14` | Lebar bar node. |

```razor
<SankeyDiagram Links="@(new[] { new SankeyLink("Ads", "Landing", 4200), new SankeyLink("Landing", "Checkout", 900) })" />
```

### Treemap

Treemap squarified dua level.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Items` | `IReadOnlyList<TreemapItem>` | `[]` | Persegi panjang; isi `Children` untuk level kedua. Wajib. |
| `Title` | `string?` | `null` | Judul chart. |
| `Width` / `Height` | `int` | `720` / `380` | Ukuran view SVG. |

```razor
<Treemap Items="@budget" />
```

### GeoAnalyticsMap

Peta di atas tile OpenStreetMap dengan gelembung yang ukurannya sesuai nilai. Seret untuk menggeser peta.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Points` | `IReadOnlyList<GeoPoint>` | `[]` | Lokasi (`Latitude`, `Longitude`, `Label`, `Value`, `Group`). Wajib. |
| `Title` | `string?` | `null` | Judul peta. |
| `Height` | `int` | `420` | Tinggi dalam piksel. |
| `ViewportWidth` | `int` | `960` | Lebar yang dipakai untuk perhitungan tile. Peta akan melebar mengikuti container-nya. |
| `MaxRadius` | `double` | `26` | Radius gelembung terbesar. |
| `TileUrl` | `string` | `"https://tile.openstreetmap.org/{z}/{x}/{y}.png"` | Template URL tile XYZ. |
| `Attribution` | `string` | kredit OpenStreetMap | HTML atribusi. |
| `OnPointSelected` | `EventCallback<GeoPoint>` | | Dipicu saat gelembung diklik. |

```razor
<GeoAnalyticsMap Points="@cities" OnPointSelected="ShowCity" />
```

### NetworkGraph

Graph force-directed. Seret node, klik untuk fokus pada sebuah node dan tetangganya.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Nodes` | `IReadOnlyList<GraphNode>` | `[]` | Node. Wajib. |
| `Edges` | `IReadOnlyList<GraphEdge>` | `[]` | Edge. Wajib. |
| `Title` | `string?` | `null` | Judul chart. |
| `Directed` | `bool` | `true` | Menggambar mata panah. |
| `ShowEdgeLabels` | `bool` | `false` | Menampilkan label edge. |
| `Width` / `Height` | `int` | `720` / `440` | Ukuran view SVG. |
| `OnNodeSelected` | `EventCallback<GraphNode>` | | Dipicu saat node diklik. |
| `SelectedNodeId` | `string?` | `null` | Menyorot sebuah node dan tetangganya, misalnya dari kotak pencarian. |

```razor
<NetworkGraph Nodes="@nodes" Edges="@edges" OnNodeSelected="Show" />
```

### KnowledgeGraph

Entitas bertipe dan relasi berlabel, dengan pencarian dan panel detail.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Nodes` | `IReadOnlyList<GraphNode>` | `[]` | Entitas; `Group` adalah tipe entitas. Wajib. |
| `Edges` | `IReadOnlyList<GraphEdge>` | `[]` | Relasi; `Label` adalah nama relasi. Wajib. |
| `Width` / `Height` | `int` | `760` / `460` | Ukuran view SVG. |

```razor
<KnowledgeGraph Nodes="@entities" Edges="@relations" />
```

### ForecastCharts

Data historis, forecast, dan confidence band dengan marker "now".

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `History` | `IReadOnlyList<double>` | `[]` | Nilai hasil observasi. Wajib. |
| `Forecast` | `IReadOnlyList<double>` | `[]` | Nilai forecast. Wajib. |
| `Lower` / `Upper` | `IReadOnlyList<double>?` | `null` | Confidence band. |
| `Title` | `string` | `"Forecast"` | Judul kartu. |
| `Subtitle` | `string?` | `null` | Subjudul kartu. |
| `YLabel` | `string?` | `null` | Label sumbu Y. |
| `XFormatter` | `Func<double, string>?` | `null` | Memformat indeks x, misalnya sebagai tanggal. |
| `Height` | `int` | `280` | Tinggi chart. |

```razor
<ForecastCharts History="@history" Forecast="@f.Forecast" Lower="@f.Lower" Upper="@f.Upper" />
```

### PredictionCharts

Nilai aktual versus prediksi beserta residual, dengan R², RMSE, dan MAE.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Actual` | `IReadOnlyList<double>` | `[]` | Nilai sebenarnya. Wajib. |
| `Predicted` | `IReadOnlyList<double>` | `[]` | Prediksi model. Wajib. |
| `Title` | `string` | `"Prediction quality"` | Judul kartu. |
| `Subtitle` | `string?` | `"Points on the dashed line are perfect predictions"` | Subjudul kartu. |

```razor
<PredictionCharts Actual="@y" Predicted="@yHat" />
```

### RealtimeCharts

Chart live yang membaca dari `IAsyncEnumerable<double>` dan menandai anomali secara rolling.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Source` | `IAsyncEnumerable<double>?` | `null` | Aliran nilai. |
| `Title` | `string` | `"Live signal"` | Judul kartu. |
| `SeriesName` | `string` | `"Value"` | Nama series. |
| `WindowSize` | `int` | `120` | Jumlah titik yang dipertahankan di layar. |
| `AnomalyZ` | `double` | `3` | Menandai titik yang berjarak lebih dari sekian standar deviasi dari rolling mean. |
| `Height` | `int` | `240` | Tinggi chart. |
| `OnAnomaly` | `EventCallback<ChartPoint>` | | Dipicu untuk setiap titik yang ditandai. |

```razor
<RealtimeCharts Source="@telemetry.ReadAllAsync()" AnomalyZ="3" OnAnomaly="Alert" />
```

---

## Blazor.AI.RAG

Paket: `Blazor.AI.RAG` · Namespace: `Blazor.AI.RAG`

```csharp
builder.Services.AddBlazorAIRag();   // KnowledgeBase over the registered IVectorIndex
```

`AddBlazorAIRag()` mendaftarkan `KnowledgeBase` scoped (satu per circuit pengguna), extractor knowledge graph, dan analis spreadsheet. Component-component di satu halaman berbagi knowledge base yang sama.

![RAG and knowledge](../screenshots/rag.jpg)

### DocumentIngestion

Mengunggah PDF, Markdown, CSV, Excel, HTML, atau teks yang ditempel, lalu memecahnya menjadi chunk, membuat embedding, dan mengindeksnya.

Tanpa parameter `Provider`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Documents"` | Judul kartu. |
| `MaxFileSizeMb` | `int` | `25` | Ukuran file maksimum. |
| `MaxFiles` | `int` | `10` | Jumlah file maksimum per unggahan. |
| `AllowPaste` | `bool` | `true` | Menampilkan opsi tempel teks. |
| `Accept` | `string` | ekstensi yang didukung | Daftar `accept` untuk input file (dari `DocumentLoader.SupportedExtensions`). |
| `OnIngested` | `EventCallback<KnowledgeDocument>` | | Dipicu untuk setiap dokumen yang selesai diindeks. |

```razor
<DocumentIngestion MaxFileSizeMb="25" OnIngested="Notify" />
```

### SmartDocumentChat

Chat di atas knowledge base. Setiap jawaban menyertakan sumbernya; klik nomornya untuk melihat kutipan teksnya.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Ask your documents"` | Judul header. |
| `Icon` | `string` | `"bi-journal-text"` | Ikon header. |
| `EmptyTitle` | `string` | `"Ask anything about your documents"` | Judul tampilan kosong. |
| `Placeholder` | `string` | `"Ask a question…"` | Placeholder input. |
| `Height` | `string` | `"560px"` | Tinggi CSS. |
| `Suggestions` | `IReadOnlyList<string>?` | `null` | Pertanyaan pembuka. |
| `DocumentId` | `string?` | `null` | Membatasi jawaban pada satu dokumen. |
| `TopK` | `int` | `5` | Jumlah passage yang diambil per pertanyaan. |
| `HeaderActions` | `RenderFragment?` | `null` | Tombol header tambahan. |
| `OnAnswer` | `EventCallback<RagAnswer>` | | Dipicu dengan jawaban beserta sitasinya. |

```razor
<SmartDocumentChat Suggestions="@questions" TopK="5" />
```

### VectorSearchBox

Pencarian passage secara semantik di knowledge base, dengan istilah yang disorot.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Query` | `string?` | `null` | Query awal. |
| `Placeholder` | `string` | `"Ask in your own words, e.g. how long do refunds take?"` | Placeholder input. |
| `DocumentId` | `string?` | `null` | Membatasi pencarian pada satu dokumen. |
| `ShowGrid` | `bool` | `false` | Menampilkan hasil dalam grid yang bisa diurutkan, bukan kartu. |
| `OnResults` | `EventCallback<IReadOnlyList<KnowledgeHit>>` | | Dipicu dengan hasil pencarian. |

```razor
<VectorSearchBox Query="@query" OnResults="Show" />
```

### SemanticResultGrid

Menampilkan hasil pencarian sebagai tabel yang bisa diurutkan, dengan ekspor CSV.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Hits` | `IReadOnlyList<KnowledgeHit>` | `[]` | Hasil yang ditampilkan. Wajib. |
| `MetadataColumns` | `IReadOnlyList<string>` | `[]` | Key metadata yang ditampilkan sebagai kolom tambahan, misalnya tag yang ditambahkan saat ingestion. |

```razor
<SemanticResultGrid Hits="@hits" MetadataColumns="@(["department"])" />
```

### CitationViewer

Me-render jawaban yang berbasis sumber: marker `[n]` diubah menjadi sitasi bernomor yang bisa dibentangkan.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Answer` | `string?` | `null` | Teks jawaban dengan marker `[n]`. |
| `Citations` | `IReadOnlyList<KnowledgeHit>` | `[]` | Sumber, sesuai urutan marker. |
| `ShowSources` | `bool` | `true` | Menampilkan daftar sumber di bawah jawaban. |

```razor
<CitationViewer Answer="@answer.Answer" Citations="@answer.Citations" />
```

### KnowledgeExplorer

Menampilkan persis apa saja yang sudah diindeks: dokumen, chunk, dan vektor.

Tanpa parameter `Provider`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Knowledge explorer"` | Judul kartu. |

```razor
<KnowledgeExplorer />
```

### PdfChat

Jatuhkan sebuah PDF lalu chat dengannya; jawaban menyertakan nomor halaman.

Mendeklarasikan `Provider` sendiri (tanpa base class).

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"PDF chat"` | Judul kartu. |
| `Provider` | `string?` | `null` | Nama provider. |
| `Height` | `string` | `"600px"` | Tinggi CSS. |
| `MaxFileSizeMb` | `int` | `40` | Ukuran PDF maksimum. |
| `Suggestions` | `IReadOnlyList<string>` | 4 pertanyaan seputar dokumen | Pertanyaan pembuka. |

```razor
<PdfChat Provider="azure" />
```

### SpreadsheetChat

Menjawab pertanyaan tentang data CSV atau Excel. Model merencanakan query-nya dan GraviFrame menghitung angkanya secara akurat.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Spreadsheet chat"` | Judul kartu. |
| `PreviewRows` | `int` | `8` | Jumlah baris di tabel pratinjau. |
| `MaxFileSizeMb` | `int` | `20` | Ukuran unggahan maksimum. |
| `SampleCsv` | `string?` | `null` | CSV opsional yang ditawarkan sebagai "Use the sample data". |
| `Frame` | `DataFrame?` | `null` | Memuat tabel secara langsung tanpa meminta unggahan. |

```razor
<SpreadsheetChat Frame="@dataFrame" />
```

### KnowledgeGraphViewer

Mengekstrak entitas dan relasi dari sebuah dokumen dan memungkinkan kamu menjelajahinya sebagai graph.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Knowledge graph"` | Judul kartu. |
| `Text` | `string?` | `null` | Teks sumber. |
| `MaxEntities` | `int` | `30` | Jumlah entitas maksimum yang diekstrak. |
| `OnExtracted` | `EventCallback<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)>` | | Dipicu dengan graph hasil ekstraksi. |

```razor
<KnowledgeGraphViewer Text="@article" MaxEntities="30" />
```

---

## Blazor.AI.Agents

Paket: `Blazor.AI.Agents` · Namespace: `Blazor.AI.Agents`

```csharp
builder.Services.AddBlazorAIAgents(tools =>
    tools.Register(LookupOrder, "lookup_order", "Order status by number"));
```

`AddBlazorAIAgents()` mendaftarkan katalog agent, tool registry, memory, runtime, orkestrator multi-agent, workflow executor, state task board, dan konektor MCP, semuanya scoped per circuit pengguna. Agent memilih model-nya di `AgentDefinition` masing-masing, sehingga sebagian besar component agent tidak punya parameter `Provider`.

![Agents](../screenshots/agents.jpg)

### AgentStudio

Ubah instruksi, model, tool, dan memory sebuah agent, lalu uji berdampingan.

Parameter: hanya `Class`.

```razor
<AgentStudio />
```

### AiAgentChat

Chat dengan sebuah agent; tool call tampil inline beserta hasil dan durasinya.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Agent` | `AgentDefinition?` | `null` | Agent yang diajak chat. Default-nya agent pertama di katalog. |
| `AllowAgentSwitch` | `bool` | `true` | Menampilkan picker agent. |
| `ShowHeader` | `bool` | `true` | Menampilkan header. |
| `Height` | `string` | `"560px"` | Tinggi CSS. |
| `Suggestions` | `IReadOnlyList<string>?` | `null` | Prompt pembuka. |
| `OnRunCompleted` | `EventCallback<AgentRun>` | | Dipicu dengan trace run lengkap. |

```razor
<AiAgentChat Agent="@catalog.Get("analyst")" OnRunCompleted="Log" />
```

### AiMultiAgentPanel

Menjalankan tim agent spesialis untuk satu task, sebagai pipeline, meja bundar, atau dirutekan oleh supervisor.

Mendeklarasikan `Provider` sendiri.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Multi-agent panel"` | Judul kartu. |
| `Task` | `string?` | `null` | Task awal. |
| `Provider` | `string?` | `null` | Nama provider. |

```razor
<AiMultiAgentPanel Task="@task" />
```

### AgentTaskBoard

Memecah sebuah tujuan menjadi task yang ditugaskan ke agent, menjalankannya, lalu memungkinkan kamu me-review dan menyetujuinya. Seret kartu antar kolom.

Mendeklarasikan `Provider` sendiri.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Provider` | `string?` | `null` | Provider yang dipakai untuk perencanaan. |

```razor
<AgentTaskBoard />
```

### AgentWorkflowDesigner

Editor node untuk workflow agent: seret node, sambungkan port, buat percabangan berdasarkan kondisi, lalu jalankan dan lihat node-node menyala.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Workflow` | `AgentWorkflow` | `AgentWorkflow.Sample()` | Workflow yang diedit dan dijalankan. |
| `Width` / `Height` | `int` | `1080` / `380` | Ukuran kanvas. |
| `OnCompleted` | `EventCallback<string>` | | Dipicu dengan output akhir sebuah run. |

```razor
<AgentWorkflowDesigner Workflow="@AgentWorkflow.Sample()" OnCompleted="Publish" />
```

### AgentDebugger

Trace lengkap setiap run agent di dalam circuit: input, tool call, hasil, token, dan error.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Agent debugger"` | Judul kartu. |

```razor
<AgentDebugger />
```

### ToolExecutionPanel

Panggil tool terdaftar mana pun secara manual; form-nya dibuat dari JSON schema tool tersebut.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Tool execution"` | Judul kartu. |

```razor
<ToolExecutionPanel />
```

### AgentMemoryViewer

Jelajahi dan cari memori jangka panjang agent berdasarkan makna.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Agent memory"` | Judul kartu. |

```razor
<AgentMemoryViewer />
```

### MCPServerExplorer

Menghubungkan server Model Context Protocol; tool-tool dari server tersebut ditambahkan ke `ToolRegistry` dan tersedia untuk setiap agent.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"MCP server explorer"` | Judul kartu. |

```razor
<MCPServerExplorer />
```

---

## Blazor.AI.Decision

Paket: `Blazor.AI.Decision` · Namespace: `Blazor.AI.Decision`

```csharp
builder.Services.AddBlazorAIDecision(o => o.ApiKey = config["TypeSafe:ApiKey"]);
```

`AddBlazorAIDecision()` mendaftarkan rule engine dan layanan keputusan TypeSafe. Tanpa API key, penilaian TypeSafe memakai simulator lokal. Rule, decision table, tree, scorecard, dan pricing dihitung di .NET; model menulis rule dari teks kebijakan dan menjelaskan hasilnya.

![Decision intelligence](../screenshots/decision.jpg)

### RuleEditor

Business rule yang mudah dibaca, divalidasi saat kamu mengetik, dan diuji terhadap fakta. Jelaskan sebuah kebijakan dan AI akan menuliskan rule-nya.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Rule editor"` | Judul kartu. |
| `Rules` | `List<BusinessRule>` | contoh rule | Rule yang diedit. |
| `Facts` | `IDictionary<string, object?>` | contoh fakta | Fakta untuk pengujian. |
| `Mode` | `RuleMode` | `RuleMode.AllMatches` | `AllMatches` menjalankan setiap rule yang cocok sesuai urutan prioritas; `FirstMatch` hanya menjalankan rule dengan prioritas tertinggi. |
| `OnEvaluated` | `EventCallback<RuleEvaluation>` | | Dipicu dengan trace evaluasi. |

```razor
<RuleEditor Rules="@rules" Facts="@facts" OnEvaluated="Apply" />

@code {
    // The same engine in code:
    // var result = new RuleEngine().Evaluate(rules, facts);
}
```

### DecisionTableEditor

Decision table bergaya DMN dengan hit policy dan sel mirip FEEL.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Table` | `DecisionTable` | `DecisionTable.LoanSample()` | Tabel yang diedit. |
| `Facts` | `IDictionary<string, object?>` | contoh fakta pinjaman | Nilai input untuk pengujian. |
| `OnEvaluated` | `EventCallback<DecisionTableResult>` | | Dipicu dengan baris yang cocok dan output-nya. |

```razor
<DecisionTableEditor Table="@DecisionTable.LoanSample()" />
```

### DecisionTreeViewer

Menampilkan decision tree dan jalur yang dilalui sebuah kasus di dalamnya.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Root` | `DecisionTreeNode` | `DecisionTreeNode.ChurnSample()` | Tree yang ditampilkan. |
| `Title` | `string` | `"Decision tree"` | Judul kartu. |
| `Subtitle` | `string?` | `"Follow the path a case takes"` | Subjudul kartu. |
| `ShowTester` | `bool` | `true` | Menampilkan editor fakta untuk menguji sebuah kasus. |
| `Facts` | `IDictionary<string, object?>` | contoh fakta | Kasus yang ditelusuri. |

```razor
<DecisionTreeViewer Root="@tree" Facts="@caseFacts" />
```

### BusinessPolicyDesigner

Mengubah teks kebijakan menjadi rule yang bisa dieksekusi, lengkap dengan pengecekan konflik, versi, dan penjelasan dalam bahasa sehari-hari.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `PolicyText` | `string?` | `null` | Kebijakan dalam bahasa sehari-hari. |
| `Facts` | `IDictionary<string, object?>` | contoh fakta | Fakta untuk menguji rule yang dihasilkan. |
| `OnPublished` | `EventCallback<IReadOnlyList<BusinessRule>>` | | Dipicu saat sebuah rule set dipublikasikan. |

```razor
<BusinessPolicyDesigner PolicyText="@policy" OnPublished="Deploy" />
```

### RiskAssessmentPanel

Scorecard berbobot yang dikombinasikan dengan penilaian TypeSafe atas pertanyaan kualitatif, diikuti usulan mitigasi dari AI.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Risk assessment"` | Judul kartu. |
| `CaseDescription` | `string` | contoh pinjaman UKM | Kasus yang dinilai. |
| `Factors` | `List<RiskFactor>` | contoh faktor | Faktor scorecard: `Value` dari 0 (tanpa risiko) sampai 10, dikalikan `Weight`. |
| `Questions` | `IReadOnlyDictionary<string, string>` | 3 contoh pertanyaan | Pertanyaan kualitatif yang dijawab TypeSafe (key ke pertanyaan), masing-masing diberi skor 0 sampai 1. |
| `JudgementWeight` | `double` | `0.4` | Porsi skor keseluruhan yang diambil dari penilaian TypeSafe; sisanya dari scorecard. |
| `OnAssessed` | `EventCallback<double>` | | Dipicu dengan skor keseluruhan. |

```razor
<RiskAssessmentPanel CaseDescription="@application"
                     Factors="@([new RiskFactor("Financial leverage", 6, 2), new RiskFactor("Payment history", 4, 2.5)])" />
```

### EligibilityEngineUI

Memeriksa pemohon terhadap kriteria wajib dan opsional, lalu menulis penjelasan yang ramah untuk pemohon.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Eligibility engine"` | Judul kartu. |
| `ProgramName` | `string` | `"KUR micro-business loan (sample)"` | Nama program. |
| `Language` | `string?` | `null` | Bahasa penjelasan. |
| `Criteria` | `List<EligibilityCriterion>` | contoh kriteria | Kriteria (`Name`, `Expression`, `Required`, `HowToMeet`). |
| `Applicant` | `IDictionary<string, object?>` | contoh pemohon | Fakta pemohon. |
| `OnChecked` | `EventCallback<bool>` | | Dipicu dengan status apakah pemohon memenuhi syarat. |

```razor
<EligibilityEngineUI Criteria="@criteria" Applicant="@applicant" Language="Bahasa Indonesia" />
```

### IntelligentApprovalWorkflow

Rule memutuskan kasus yang jelas, AI menilai sisanya, dan manusia me-review keputusan dengan tingkat keyakinan rendah. Setiap keputusan dicatat ke audit trail.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Intelligent approval workflow"` | Judul kartu. |
| `AutoApproveLimit` | `double` | `2_000_000` | Permintaan sampai jumlah ini disetujui oleh rule. |
| `AutoRejectAbove` | `double` | `500_000_000` | Permintaan di atas jumlah ini ditolak oleh rule. |
| `ConfidenceThreshold` | `double` | `0.75` | Keputusan AI dengan tingkat keyakinan di bawah nilai ini diteruskan ke review manusia. |
| `OnDecided` | `EventCallback<string>` | | Dipicu dengan keputusan. |

```razor
<IntelligentApprovalWorkflow AutoApproveLimit="2000000" ConfidenceThreshold="0.75" />
```

### DynamicPricingEngine

Mencari harga dengan profit optimal di bawah elastisitas harga konstan, lalu menerapkan batasan margin, harga kompetitor, dan inventori.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Dynamic pricing"` | Judul kartu. |
| `Inputs` | `PricingInputs` | `new()` | Biaya per unit, harga dan permintaan acuan, harga kompetitor, elastisitas, minggu inventori, margin minimum, dan premi maksimum di atas kompetitor. |
| `OnPriced` | `EventCallback<PricingResult>` | | Dipicu dengan harga optimal, harga akhir, unit, profit, dan penyesuaian. |

```razor
<DynamicPricingEngine Inputs="@(new PricingInputs { UnitCost = 42_000, Elasticity = -2.2 })" OnPriced="UpdateCatalog" />
```

### FactsEditor

Mengedit sekumpulan fakta (nama ke nilai). Angka dan true/false otomatis diberi tipe yang sesuai. Dipakai oleh component decision lainnya.

Tanpa panggilan AI.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Facts` | `IDictionary<string, object?>` | kosong | Fakta yang diedit. Mendukung `@bind-Facts`. Wajib. |
| `FactsChanged` | `EventCallback<IDictionary<string, object?>>` | | Dipicu setelah setiap perubahan. |
| `Label` | `string` | `"Facts"` | Label di atas daftar. |

```razor
<FactsEditor @bind-Facts="facts" Label="Applicant" />
```

---

## Blazor.AI.Analytics

Paket: `Blazor.AI.Analytics` · Namespace: `Blazor.AI.Analytics`

```csharp
builder.Services.AddBlazorAIAnalytics();
```

`AddBlazorAIAnalytics()` mendaftarkan `AnalyticsWorkspace` dan `RlWorkspace` per pengguna. Component yang mewarisi `AnalyticsComponentBase` berbagi dataset workspace (dipilih dengan `DatasetPicker`) kecuali kamu mengisi `Dataset`. Training, statistik, dan reinforcement learning berjalan di .NET dengan Gravicode.Science dan GraviFrame; model menulis ringkasan dan memberi nama segmen.

### Machine learning

![Machine learning](../screenshots/ml.jpg)

### DatasetPicker

Memilih dataset workspace: contoh bawaan atau CSV yang diunggah, beserta kolom target dan task-nya. Semua component analytics yang terikat ke workspace akan bereaksi terhadap perubahan ini.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `ShowTarget` | `bool` | `true` | Menampilkan picker kolom target dan task. |

```razor
<DatasetPicker />
<ModelTrainer />
```

### DatasetExplorer

Membuat profil kolom, distribusi, dan baris, serta menulis ringkasan AI tentang dataset.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Dataset explorer"` | Judul kartu. |

```razor
<DatasetExplorer />   @* shares AnalyticsWorkspace.Dataset with the other components *@
```

### FeatureEngineeringDesigner

Membangun feature langkah demi langkah dengan undo; AI menyarankan feature dari profil kolom.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Feature engineering"` | Judul kartu. |

```razor
<FeatureEngineeringDesigner />
```

### AutoMLStudio

Melakukan cross-validation untuk setiap algoritma yang cocok pada dataset dan mengurutkannya.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"AutoML studio"` | Judul kartu. |
| `Folds` | `int` | `5` | Jumlah fold cross-validation. |
| `OnBestModel` | `EventCallback<ModelSpec>` | | Dipicu dengan model terbaik. |

```razor
<AutoMLStudio Folds="5" OnBestModel="Train" />
```

### ModelTrainer

Melatih model dengan algoritma, hyperparameter, dan feature pilihanmu, lalu menampilkan metrik, feature importance, dan prediksi langsung.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Model trainer"` | Judul kartu. |
| `Task` | `MlTask?` | `null` | Memaksa task tertentu (`Classification`, `Regression`, `Clustering`, `TimeSeries`); jika tidak, task dari dataset yang dipakai. |
| `Algorithm` | `string?` | `null` | Algoritma yang dipilih di awal. |
| `OnTrained` | `EventCallback<TrainingResult>` | | Dipicu dengan model yang sudah dilatih beserta metriknya. |

```razor
<ModelTrainer Task="MlTask.Classification" Algorithm="Random forest" OnTrained="Save" />
```

### ClassificationWorkbench

Melatih classifier dan menampilkan confusion matrix, kurva ROC, dan classification report.

Mendeklarasikan `Provider` sendiri (tanpa base class).

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Classification workbench"` | Judul kartu. |
| `Dataset` | `AnalyticsDataset?` | `null` | Dataset; `null` memakai dataset workspace. |
| `Provider` | `string?` | `null` | Nama provider. |

```razor
<ClassificationWorkbench Dataset="@(new AnalyticsDataset("Churn", frame, "churn"))" />
```

### RegressionWorkbench

Melatih regressor dan menampilkan nilai aktual versus prediksi serta residual pada data held-out.

Mendeklarasikan `Provider` sendiri (tanpa base class).

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Regression workbench"` | Judul kartu. |
| `Dataset` | `AnalyticsDataset?` | `null` | Dataset; `null` memakai dataset workspace. |
| `Provider` | `string?` | `null` | Nama provider. |

```razor
<RegressionWorkbench Dataset="@dataset" />
```

### ClusteringWorkbench

Mengelompokkan baris dengan K-Means, GMM, hierarchical clustering, atau DBSCAN; AI memberi nama segmen-segmennya.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Clustering workbench"` | Judul kartu. |
| `OnClustered` | `EventCallback<ClusteringResult>` | | Dipicu dengan penetapan cluster. |

```razor
<ClusteringWorkbench Dataset="@segments" OnClustered="Tag" />
```

### ForecastingWorkbench

Membuat forecast sebuah series dengan Holt-Winters dan metode sejenis, lengkap dengan confidence band dan perbandingan metode.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Forecasting workbench"` | Judul kartu. |

```razor
<ForecastingWorkbench Dataset="@sales" />
```

### AnomalyDetectionWorkbench

Menemukan outlier dengan robust z-score, IQR fence, atau one-class SVM.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Anomaly detection"` | Judul kartu. |
| `OnDetected` | `EventCallback<IReadOnlyList<int>>` | | Dipicu dengan indeks baris anomali. |

```razor
<AnomalyDetectionWorkbench Dataset="@telemetry" OnDetected="Alert" />
```

### RecommendationWorkbench

Collaborative filtering berbasis item pada matriks rating yang bisa kamu edit secara langsung.

Tanpa parameter `Provider`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Recommendation workbench"` | Judul kartu. |
| `Users` | `IReadOnlyList<string>` | contoh pengguna | Label baris. |
| `Items` | `IReadOnlyList<string>` | contoh item | Label kolom. |
| `Ratings` | `double[,]` | contoh rating | Matriks pengguna × item. |
| `Top` | `int` | `4` | Jumlah rekomendasi per pengguna. |

```razor
<RecommendationWorkbench Users="@users" Items="@items" Ratings="@ratings" />
```

### Data science

![Data science](../screenshots/data-science.jpg)

### CorrelationMatrixViewer

Korelasi Pearson atau Spearman, pasangan dengan korelasi terkuat, dan scatter plot untuk pasangan yang dipilih.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Correlation matrix"` | Judul kartu. |

```razor
<CorrelationMatrixViewer Dataset="@dataset" />
```

### StatisticalAnalysisPanel

Statistik deskriptif: pemusatan, sebaran, bentuk distribusi, bootstrap confidence interval, KDE dan box plot, serta ringkasan per kelompok.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Statistical analysis"` | Judul kartu. |

```razor
<StatisticalAnalysisPanel Dataset="@dataset" />
```

### HypothesisTestingPanel

Menjalankan uji t, ANOVA, dan uji chi-square pada kolom-kolom dataset.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Hypothesis testing"` | Judul kartu. |

```razor
<HypothesisTestingPanel Dataset="@dataset" />
```

### BayesianAnalysisPanel

Analisis A/B Bayesian dengan posterior Beta, probabilitas B mengalahkan A, expected lift, dan expected loss.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Bayesian A/B analysis"` | Judul kartu. |
| `SuccessA` / `TotalA` | `int` | `120` / `1_000` | Konversi dan percobaan untuk varian A. |
| `SuccessB` / `TotalB` | `int` | `151` / `1_000` | Konversi dan percobaan untuk varian B. |
| `PriorAlpha` / `PriorBeta` | `double` | `1` / `1` | Prior Beta. |
| `DecisionThreshold` | `double` | `0.95` | Merekomendasikan B hanya jika P(B > A) melebihi nilai ini. |

```razor
<BayesianAnalysisPanel SuccessA="120" TotalA="1000" SuccessB="151" TotalB="1000" />
```

### PCAVisualizer

Principal component analysis dengan scree plot, proyeksi, dan loading.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"PCA visualizer"` | Judul kartu. |

```razor
<PCAVisualizer Dataset="@dataset" />
```

### DimensionReductionStudio

Membandingkan proyeksi PCA, LDA, dan t-SNE secara berdampingan.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Dimension reduction studio"` | Judul kartu. |

```razor
<DimensionReductionStudio Dataset="@dataset" />
```

### TimeSeriesAnalyzer

Moving average, dekomposisi musiman, dan autokorelasi untuk sebuah time series.

Base: `AnalyticsComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Time series analyzer"` | Judul kartu. |

```razor
<TimeSeriesAnalyzer Dataset="@sales" />
```

### SignalProcessingStudio

Susun sebuah sinyal, periksa spektrumnya, lalu filter.

Tanpa parameter `Provider`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Signal processing studio"` | Judul kartu. |
| `Input` | `IReadOnlyList<double>?` | `null` | Menganalisis sinyal ini, bukan sinyal sintetis. |

```razor
<SignalProcessingStudio Input="@vibrationSamples" />
```

### Reinforcement learning

![Reinforcement learning](../screenshots/rl.jpg)

Component RL berbagi `RlWorkspace`, jadi environment yang dirancang di satu component bisa dilatih dan diputar ulang di component lainnya.

### EnvironmentDesigner

Gambar sebuah grid world: dinding, jebakan, titik awal, dan tujuan.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Environment designer"` | Judul kartu. |

```razor
<EnvironmentDesigner />
```

### RewardDesigner

Ubah reward dan probabilitas tergelincir, latih ulang, lalu lihat bagaimana jalur yang dipelajari berubah.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Reward designer"` | Judul kartu. |
| `Episodes` | `int` | `400` | Jumlah episode training per pelatihan ulang. |

```razor
<RewardDesigner Episodes="400" />
```

### RLTrainingStudio

Melatih agent di grid world milikmu atau di environment klasik: CartPole, MountainCar, LunarLander, Pendulum, trading, dan supply chain.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"RL training studio"` | Judul kartu. |
| `OnTrained` | `EventCallback<TrainingSession>` | | Dipicu saat sebuah training run selesai. |

```razor
<RLTrainingStudio OnTrained="Save" />
```

### PolicyVisualizer

Menampilkan aksi terbaik dan nilai setiap sel untuk agent Q-learning.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Policy visualizer"` | Judul kartu. |

```razor
<PolicyVisualizer />
```

### SimulationRunner

Memutar ulang agent mana pun yang sudah dilatih, langkah demi langkah.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Simulation runner"` | Judul kartu. |

```razor
<SimulationRunner />
```

### AgentPerformanceTracker

Membandingkan kurva pembelajaran, stabilitas, dan waktu training antar training run.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Agent performance tracker"` | Judul kartu. |

```razor
<AgentPerformanceTracker />
```

### StrategyOptimizer

Multi-armed bandit untuk memilih strategi secara live: ε-greedy, UCB1, dan Thompson sampling dibandingkan dengan pembagian A/B biasa.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Strategy optimizer"` | Judul kartu. |

```razor
<StrategyOptimizer />
```

### RlScene

Me-render satu frame simulasi untuk environment klasik (CartPole, MountainCar, LunarLander, Pendulum) dan grid world. Dipakai oleh `SimulationRunner` dan `RewardDesigner`.

Tanpa panggilan AI. Tanpa parameter `Class`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `State` | `IReadOnlyDictionary<string, double>` | kosong | State environment, misalnya `x` dan `angle` untuk CartPole. Wajib. |
| `Environment` | `string` | `""` | Nama environment. |
| `Tiles` | `Func<int, int, GridTile>?` | `null` | Tata letak tile untuk grid world. |
| `Trail` | `IReadOnlyList<(int X, int Y)>?` | `null` | Sel yang sudah dikunjungi, digambar sebagai jejak. |

```razor
<RlScene Environment="CartPole" State="@frame.State" />
```

---

## Blazor.AI.LocalModels

Paket: `Blazor.AI.LocalModels` · Namespace: `Blazor.AI.LocalModels`

```csharp
builder.Services.AddBlazorAILocalModels(o => o.HuggingFaceToken = config["HuggingFace:Token"]);

// Optional: use local models as a provider everywhere (chat, RAG, semantic search).
builder.Services.AddBlazorAI().AddLocalTransformers(embeddingModel: "sentence-transformers/all-MiniLM-L6-v2");
```

`AddBlazorAILocalModels()` mendaftarkan layanan Hugging Face Hub, client Inference API yang di-hosting, dan registry model lokal bersama. Component yang mewarisi `HfComponentBase` menjalankan model secara lokal di CPU dengan HF.Net (diunduh sekali, setelah itu privat dan bisa offline) atau di Hugging Face Inference API, dipilih lewat `Engine`. Id model default diambil dari `LocalModelOptions`.

![Hugging Face](../screenshots/huggingface.jpg)

### ModelHubBrowser

Mencari di Hugging Face Hub berdasarkan kata kunci dan task, lalu menampilkan file, ukuran, dan format bobot setiap model.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Model hub browser"` | Judul kartu. |
| `OnModelSelected` | `EventCallback<string>` | | Dipicu dengan id model yang dipilih. |

```razor
<ModelHubBrowser OnModelSelected="Use" />
```

### ModelDownloader

Mengunduh model lebih awal dengan indikator progres, dan mengelola model mana saja yang dimuat di memori.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Model downloader"` | Judul kartu. |
| `RepoId` | `string?` | `"sentence-transformers/all-MiniLM-L6-v2"` | Model yang diunduh. |
| `OnDownloaded` | `EventCallback<string>` | | Dipicu dengan id model saat unduhan selesai. |

```razor
<ModelDownloader RepoId="sentence-transformers/all-MiniLM-L6-v2" />
```

### InferencePlayground

Mencoba klasifikasi, NER, fill-mask, question answering, kemiripan, embedding, dan text generation lokal di satu tempat.

Base: `HfComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Inference playground"` | Judul kartu. |

```razor
<InferencePlayground />
```

### SentimentAnalysisPanel

Sentimen untuk satu teks atau sekumpulan teks (satu per baris), dengan distribusi keseluruhan.

Base: `HfComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Sentiment analysis"` | Judul kartu. |
| `Text` | `string?` | 3 contoh ulasan | Teks input, satu item per baris. |
| `OnAnalysed` | `EventCallback<IReadOnlyList<(string Text, string Label, double Score)>>` | | Dipicu dengan satu hasil per baris. |

```razor
<SentimentAnalysisPanel Model="w11wo/indonesian-roberta-base-sentiment-classifier" />
```

### TextClassificationPanel

Mengklasifikasi teks dengan label dari model yang sudah di-fine-tune, atau secara zero-shot dengan label milikmu sendiri.

Base: `HfComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Text classification"` | Judul kartu. |
| `Text` | `string?` | contoh pesan support | Teks input. |
| `OnClassified` | `EventCallback<IReadOnlyList<LabelScore>>` | | Dipicu dengan skor label. |

```razor
<TextClassificationPanel Engine="InferenceEngine.Local" />
```

### NERViewer

Named entity recognition dengan nama orang, organisasi, dan tempat yang disorot inline.

Base: `HfComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Named entity recognition"` | Judul kartu. |
| `Text` | `string?` | contoh kalimat | Teks input. |
| `OnExtracted` | `EventCallback<IReadOnlyList<EntitySpan>>` | | Dipicu dengan entitas yang ditemukan. |

```razor
<NERViewer Model="cahya/bert-base-indonesian-NER" />
```

### TextEmbeddingStudio

Membuat embedding kalimat dan menampilkan heatmap kemiripan serta peta 2-D.

Base: `HfComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Text embedding studio"` | Judul kartu. |
| `Text` | `string?` | 5 contoh kalimat | Kalimat, satu per baris. |

```razor
<TextEmbeddingStudio />
```

### ImageClassificationPanel

Mengklasifikasi gambar ke dalam kelas ImageNet (ViT) atau label milikmu sendiri (CLIP zero-shot).

Base: `HfComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Image classification"` | Judul kartu. |
| `MaxImageMb` | `int` | `8` | Ukuran gambar maksimum. |
| `OnClassified` | `EventCallback<IReadOnlyList<LabelScore>>` | | Dipicu dengan skor label. |

```razor
<ImageClassificationPanel />
```

### OCRPanel

Membaca teks dari gambar dengan TrOCR lokal (baris teks), atau dengan vision LLM untuk dokumen lengkap dan tabel.

Base: `HfComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"OCR"` | Judul kartu. |
| `MaxImageMb` | `int` | `10` | Ukuran gambar maksimum. |
| `OnRecognized` | `EventCallback<string>` | | Dipicu dengan teks yang dikenali. |

```razor
<OCRPanel Provider="azure" OnRecognized="Save" />
```

### SpeechRecognitionPanel

Dikte langsung dengan speech recognition bawaan browser, atau Whisper untuk file audio yang diunggah.

Base: `HfComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Speech recognition"` | Judul kartu. |
| `OnTranscribed` | `EventCallback<string>` | | Dipicu dengan hasil transkripsi. |

```razor
<SpeechRecognitionPanel OnTranscribed="Fill" />
```

### TextToSpeechPanel

Membacakan teks dengan suara bawaan browser (offline) atau model TTS neural.

Base: `HfComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Text to speech"` | Judul kartu. |
| `Text` | `string?` | contoh salam dalam Bahasa Indonesia | Teks yang dibacakan. |

```razor
<TextToSpeechPanel Text="@announcement" />
```

### EngineSelector

Picker engine dan id model yang dipakai bersama oleh component Hugging Face.

Tanpa panggilan AI. Tanpa parameter `Class`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Engine` | `InferenceEngine` | `InferenceEngine.Local` | `Local` atau `HostedApi`. Mendukung `@bind-Engine`. |
| `EngineChanged` | `EventCallback<InferenceEngine>` | | Dipicu saat engine berubah. |
| `Model` | `string?` | `null` | Id model. Mendukung `@bind-Model`. |
| `ModelChanged` | `EventCallback<string?>` | | Dipicu saat id model berubah. |
| `Suggestions` | `IReadOnlyList<string>?` | `null` | Id model yang ditawarkan di drop-down. |

```razor
<EngineSelector @bind-Engine="engine" @bind-Model="model" Suggestions="@(["dslim/distilbert-NER"])" />
```

---

## Blazor.AI.Bio

Paket: `Blazor.AI.Bio` · Namespace: `Blazor.AI.Bio`

```csharp
builder.Services.AddBlazorAIAgents();
builder.Services.AddBlazorAIBio();
```

`AddBlazorAIBio()` membutuhkan `AddBlazorAIAgents()`: method ini menambahkan tool Biotech.Net ke `ToolRegistry` setiap pengguna, sehingga agent dan `BioCopilot` bisa memanggilnya. Analisis berjalan secara lokal dengan Biotech.Net. Langkah opsional "Explain with AI" hanya mengirim hasil perhitungan ke model, tidak pernah input mentahnya. Component yang mewarisi `BioComponentBase` menerima `Audience` dan `Language` untuk penjelasan tersebut.

```razor
@using Blazor.AI.Bio
```

![Biology analysis](../screenshots/bio.jpg)

### DnaSequenceAnalyzer

Komposisi DNA, berat molekul, Tm, profil GC, peta ORF, translasi enam frame, dan pencarian motif IUPAC.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"DNA sequence analyzer"` | Judul kartu. |
| `Sequence` | `string?` | `BioSamples.Egfp` | Residu mentah atau FASTA. |
| `OnAnalysed` | `EventCallback<Sequence>` | | Dipicu dengan sekuens hasil parsing. |

```razor
<DnaSequenceAnalyzer Sequence="@construct"
                     Audience="an undergraduate lab class"
                     Language="Bahasa Indonesia"
                     OnAnalysed="seq => lastLength = seq.Length" />
```

### RestrictionMapper

Menemukan enzim pemotong tunggal (single cutter), menggambar peta situs potong, dan menyimulasikan pita pada gel agarosa 1%.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Restriction mapper"` | Judul kartu. |
| `Sequence` | `string?` | `BioSamples.Egfp` | Sekuens DNA atau FASTA. |

```razor
<RestrictionMapper Sequence="@plasmid" Title="pUC19 digest" />
```

### SequenceAligner

Pairwise alignment global, lokal, atau semi-global dengan skoring BLOSUM atau PAM.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Pairwise sequence aligner"` | Judul kartu. |
| `Target` | `string?` | `BioSamples.HemoglobinBeta` | Sekuens referensi. |
| `Query` | `string?` | hemoglobin α manusia | Sekuens yang di-align. |

```razor
<SequenceAligner Target="@reference" Query="@variant" />
```

### MultipleAlignmentViewer

Multiple sequence alignment progresif dengan track konservasi, heatmap identitas, dan pohon neighbour-joining.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Multiple alignment & phylogeny"` | Judul kartu. |
| `Fasta` | `string?` | `BioSamples.CytochromeC` | FASTA dengan banyak record. |

```razor
<MultipleAlignmentViewer Fasta="@orthologues" />
```

### PhyloTree

Menggambar pohon filogenetik. Dipakai oleh `MultipleAlignmentViewer`; kamu bisa memberikan pohon Newick apa pun yang sudah di-parse.

Tanpa panggilan AI. Tanpa parameter `Class`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Tree` | `PhylogeneticTree` | | Pohon yang digambar. Wajib. |
| `Width` | `int` | `480` | Lebar gambar. |
| `RowHeight` | `int` | `28` | Tinggi per daun (leaf). |

```razor
<PhyloTree Tree="@(PhylogeneticTree.ParseNewick("((A:0.1,B:0.2):0.05,C:0.3);"))" />
```

### ProteinAnalyzer

Properti protein ala ProtParam, kurva hidropati dan muatan, petunjuk signal peptide dan transmembran, serta peptida triptik untuk spektrometri massa.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Protein analyzer"` | Judul kartu. |
| `Sequence` | `string?` | `BioSamples.Insulin` | Sekuens protein atau FASTA. |

```razor
<ProteinAnalyzer Sequence="@BioSamples.Insulin" Audience="a protein purification scientist" />
```

### MoleculeExplorer

Dari SMILES ke struktur 2-D, descriptor, aturan Lipinski, Veber, Ghose, dan Egan, QED, estimasi ADMET, serta peringatan PAINS/Brenk.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Molecule explorer"` | Judul kartu. |
| `Smiles` | `string?` | contoh molekul pertama | String SMILES. |

```razor
<MoleculeExplorer Smiles="CC(=O)Oc1ccccc1C(=O)O" Title="Aspirin" />
```

### CrisprGuideDesigner

Mencari situs PAM di kedua untai (SpCas9 dan variannya), memberi skor aktivitas on-target, memeriksa deretan poly-T, dan mencetak oligo kloning U6.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"CRISPR guide designer"` | Judul kartu. |
| `Target` | `string?` | `BioSamples.Egfp` | DNA target. |

```razor
<CrisprGuideDesigner Target="@exon2" Audience="a gene-editing core facility" />
```

### PrimerDesigner

Merancang pasangan primer PCR yang diurutkan berdasarkan kecocokan Tm, GC clamp, hairpin, dan dimer, sesuai ukuran produk yang kamu tentukan.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"PCR primer designer"` | Judul kartu. |
| `Template` | `string?` | `BioSamples.Egfp` | DNA template. |

```razor
<PrimerDesigner Template="@amplicon" />
```

### CodonOptimizer

Melakukan back-translation protein atau mengode ulang CDS untuk E. coli, ragi, CHO, Pichia, atau sel manusia, sambil memantau CAI dan kandungan GC.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Codon optimizer"` | Judul kartu. |
| `Input` | `string?` | `BioSamples.Egfp` | Sekuens protein atau DNA penyandi. |

```razor
<CodonOptimizer Input="@protein" />
```

### DifferentialExpressionExplorer

Pengujian negative-binomial ala DESeq2 untuk count RNA-seq, dengan volcano plot dan MA plot.

Base: `BioComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Differential expression explorer"` | Judul kartu. |
| `Counts` | `Dataset?` | `null` | Count berupa gen × sampel dengan anotasi `condition`. `null` memakai eksperimen sintetis. |
| `Reference` | `string` | `"control"` | Kondisi referensi. |
| `Treatment` | `string` | `"treated"` | Kondisi perlakuan. |

```razor
<DifferentialExpressionExplorer Counts="@counts" Reference="control" Treatment="treated" />
```

### BioCopilot

Chat yang memanggil tool Biotech.Net (statistik DNA, ProtParam, ADMET, CRISPR, primer, situs restriksi) dan menyebutkan tool mana yang menghasilkan setiap angka.

Mendeklarasikan `Provider` sendiri (tanpa base class).

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Height` | `string` | `"520px"` | Tinggi CSS. |
| `Provider` | `string?` | `null` | Nama provider. |
| `Suggestions` | `IReadOnlyList<string>` | prompt pembuka seputar biologi | Prompt pembuka. |

```razor
<BioCopilot Height="480px" Provider="anthropic" />
```

### SequenceInput

Kotak teks sekuens yang menerima residu mentah atau FASTA, bisa memuat file FASTA, dan menawarkan preset contoh. Dipakai oleh component Bio.

Tanpa panggilan AI. Tanpa parameter `Class`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Value` | `string?` | `null` | Teks sekuens. Mendukung `@bind-Value`. |
| `ValueChanged` | `EventCallback<string?>` | | Dipicu saat teks berubah. |
| `Label` | `string` | `"Sequence"` | Label field. |
| `Rows` | `int` | `6` | Jumlah baris yang terlihat. |
| `Placeholder` | `string` | `"Paste residues or FASTA (>id on the first line)"` | Teks placeholder. |
| `Samples` | `IReadOnlyList<(string Name, string Value)>` | `[]` | Sekuens preset yang ditawarkan sebagai tombol. |

```razor
<SequenceInput @bind-Value="dna" Samples="@([("EGFP", BioSamples.Egfp)])" />
```

---

## Blazor.AI.ThreeD

Paket: `Blazor.AI.ThreeD` · Namespace: `Blazor.AI.ThreeD`

```csharp
builder.Services.AddBlazorAIThreeD();
```

```html
<link rel="stylesheet" href="_content/Blazor.AI.ThreeD/blazor-ai-3d.css" />
```

`AddBlazorAIThreeD()` mendaftarkan `CompanionMemoryStore` per pengguna yang dipakai `AICompanion3D`. Component-component ini dibangun di atas `AddBlazorAIComponents()`. Component yang memakai ulang paket lain juga membutuhkan paket tersebut terdaftar; `AgentStudio3D` dan `MultiAgentArena` meng-inject service agent sehingga butuh `AddBlazorAIAgents()`. Three.js sudah dibundel di dalam paket. Lihat [Component 3D](threed.md) untuk model scene, aset, dan cara setup.

```razor
@using Blazor.AI.ThreeD
```

### Parameter umum 3D

Sebagian besar component mewarisi `ThreeDComponentBase`, yang mewarisi `AiComponentBase`. Component tersebut menerima [parameter umum](#parameter-umum) (`Provider`, `Class`, `OnError`, atribut yang tidak cocok) ditambah:

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Height` | `string` | `"520px"` | Tinggi CSS viewport 3D. |
| `Language` | `string?` | `null` | Bahasa narasi AI. `null` mengikuti default model. |

Digital twin mewarisi `TwinComponentBase`, yang menambahkan:

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `UpdatesPerSecond` | `double` | `2` | Langkah simulasi per detik, dibatasi ke 0.5–5. |
| `AutoStart` | `bool` | `true` | Memulai simulasi live setelah render pertama. |

Geometri, fisika, statistik, dan training dijalankan di .NET; model hanya membuat rencana atau narasi dari fakta hasil perhitungan. Setiap component punya fallback lokal, jadi tetap berjalan dengan model simulasi offline.

### Inti

### Scene3D

Host Three.js yang menjadi dasar semua component 3D: objek deklaratif, picking, gerakan kamera, dan overlay HTML. Lihat [Model scene](threed.md#model-scene) untuk method-method-nya.

Tanpa panggilan AI. Tidak mewarisi base class.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Objects` | `IReadOnlyList<Object3D>` | `[]` | Objek yang ditampilkan. Berikan list baru untuk memperbarui; objek di-diff berdasarkan `Id`. |
| `Options` | `Scene3DOptions` | `new()` | Background, kamera, kontrol, pencahayaan, bayangan, ground, grid, dan fog. |
| `Height` | `string` | `"480px"` | Tinggi CSS. |
| `Class` | `string?` | `null` | Kelas CSS tambahan untuk elemen root. |
| `ChildContent` | `RenderFragment?` | `null` | Overlay HTML (HUD) yang digambar di atas scene. |
| `OnPick` | `EventCallback<PickEvent>` | | Dipicu saat objek yang bisa di-pick diklik. |
| `OnModelLoaded` | `EventCallback<ModelInfo>` | | Dipicu saat model glTF selesai dimuat, beserta clip, morph, dan jumlah segitiganya. |
| `OnSpeechEnded` | `EventCallback<string>` | | Dipicu dengan id objek saat `SpeakAsync` selesai. |
| `OnReady` | `EventCallback` | | Dipicu saat scene selesai diinisialisasi. |

Juga menerima atribut yang tidak cocok.

```razor
<Scene3D @ref="scene" Objects="@objects" Options="@options" Height="420px" OnPick="Picked">
    <div class="bai3d-panel">Overlay HTML</div>
</Scene3D>
```

### ModelViewer3D

Viewer glTF/GLB dengan orbit, animation clip, morph target, wireframe, jumlah segitiga, dan screenshot.

Tanpa panggilan AI. Tidak mewarisi base class.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Model viewer"` | Judul kartu. |
| `Url` | `string` | `ThreeDAssets.GuideBot` | URL model. |
| `FitHeight` | `double` | `1.7` | Menskalakan model ke tinggi ini dalam satuan scene. |
| `Height` | `string` | `"420px"` | Tinggi CSS. |
| `AutoRotate` | `bool` | `false` | Memutar kamera mengelilingi model. |
| `Class` | `string?` | `null` | Kelas CSS tambahan. |

```razor
<ModelViewer3D Url="@ThreeDAssets.GuideBot" FitHeight="1.7" AutoRotate="true" />
```

### Asisten

![Talking avatar](../screenshots/3d-aiavatar3d.jpg)

![Tour guide](../screenshots/3d-aitourguide3d.jpg)

### AIAvatar3D

Avatar 3D yang bisa bicara: input suara atau teks, balasan AI dengan emosi dan gestur, lalu output suara dengan lip sync.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Name` | `string` | `"Kawi"` | Nama avatar. |
| `Persona` | `string` | `"A friendly guide who answers briefly and warmly"` | Kepribadian yang dipakai di system prompt default. |
| `SystemPrompt` | `string?` | `null` | Menggantikan system prompt default yang disusun dari `Name` dan `Persona`. |
| `Placeholder` | `string` | `"Say hello, ask anything…"` | Placeholder input. |
| `Greeting` | `string?` | `"Hi! I'm Kawi. Ask me anything, or press the mic and talk to me."` | Pesan pertama. `null` tidak menampilkan apa pun. |
| `SpeechLanguage` | `string` | `"en-US"` | Bahasa BCP-47 untuk input dan output suara, misalnya `"id-ID"`. |
| `ModelUrl` | `string` | `ThreeDAssets.GuideBot` | Model avatar. |
| `OnReply` | `EventCallback<string>` | | Dipicu dengan setiap balasan. |

```razor
<AIAvatar3D Name="Kawi" Persona="a museum guide for Borobudur" SpeechLanguage="id-ID" />
```

### AICompanion3D

Pendamping AI dengan memory, kepribadian, tujuan, dan kondisi emosi yang terlihat; memory-nya mengorbit di sekitar robot.

Base: `ThreeDComponentBase`. Memakai `CompanionMemoryStore`, yang didaftarkan oleh `AddBlazorAIThreeD()`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Name` | `string` | `"Sekar"` | Nama pendamping. |
| `SpeechLanguage` | `string` | `"en-US"` | Bahasa BCP-47 untuk suara. |
| `Greeting` | `string` | `"Hi, I'm Sekar. I remember what you tell me and help with your goals. What should I call you?"` | Pesan pertama. |

```razor
<AICompanion3D Name="Sekar" SpeechLanguage="id-ID" Greeting="Halo! Aku Sekar. Siapa namamu?" />
```

### AITourGuide3D

Robot pemandu berjalan di antara titik-titik menarik, menunjuk setiap titik, dan menarasikannya dengan suara ber-lip sync.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Name` | `string` | `"Kawi"` | Nama pemandu. |
| `Tour` | `string` | `"Museum"` | Tur preset: `Museum`, `Smart City`, `Campus`, `Factory`, atau `Tourism`. |
| `Preset` | `TourPreset?` | `null` | Tur kustom (scenery dan pemberhentian) yang dipakai sebagai pengganti `Tour`. |
| `SpeechLanguage` | `string` | `"en-US"` | Bahasa BCP-47 untuk suara. |
| `OnStopReached` | `EventCallback<TourStop>` | | Dipicu saat pemandu tiba di sebuah pemberhentian. |

```razor
<AITourGuide3D Tour="Museum" SpeechLanguage="id-ID" OnStopReached="stop => Log(stop.Name)" />
```

### Visualisasi

### AI3DChart

Chart 3D bar, scatter, bubble, surface, dan box dengan insight yang ditulis AI.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Regional sales"` | Judul kartu. |
| `Mode` | `string` | `"Bars"` | `Bars`, `Scatter`, `Bubbles`, `Surface`, atau `Statistical`. |
| `Categories` | `IReadOnlyList<string>` | `["Q1", "Q2", "Q3", "Q4"]` | Kategori bar (sumbu X). |
| `Series` | `IReadOnlyList<string>` | empat nama pulau | Series bar (sumbu Z). |
| `Values` | `double[][]` | contoh data penjualan | Nilai bar dalam bentuk `[series][category]`. |
| `Points` | `IReadOnlyList<(double X, double Y, double Z, double Size, int Group)>?` | `null` | Titik untuk scatter dan bubble. `null` memakai titik contoh. |
| `Surface` | `Func<double, double, double>?` | `null` | Fungsi surface z = f(x, y) yang di-sample pada [-1, 1]². `null` memakai fungsi contoh. |
| `OnSelect` | `EventCallback<PickEvent>` | | Dipicu saat bar atau titik diklik. |

```razor
<AI3DChart Title="Sales by island" Mode="Bars"
           Categories="@(["Q1","Q2","Q3","Q4"])" Series="@islands" Values="@sales" />
```

### AIClusterExplorer

Menjalankan clustering KMeans, DBSCAN, HDBSCAN, atau hierarchical, menampilkan cluster dalam 3D, dan meminta AI memberi nama cluster.

Base: `ThreeDComponentBase`. Memakai `AnalyticsDataset` dari Blazor.AI.Analytics.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Cluster explorer"` | Judul kartu. |
| `Algorithm` | `ClusterAlgorithm` | `ClusterAlgorithm.KMeans` | `KMeans`, `Dbscan`, `Hdbscan`, atau `Hierarchical`. |
| `K` | `int` | `4` | Jumlah cluster untuk KMeans dan hierarchical. |
| `Epsilon` | `double` | `0.8` | Radius tetangga untuk DBSCAN. |
| `MinClusterSize` | `int` | `15` | Ukuran cluster minimum untuk HDBSCAN. |
| `Dataset` | `AnalyticsDataset?` | `null` | Data milikmu sendiri; kolom numerik yang di-cluster. `null` memakai sampel bawaan. |
| `OnClustered` | `EventCallback<ClusterLayout>` | | Dipicu dengan label, posisi, dan silhouette score. |

```razor
<AIClusterExplorer Algorithm="ClusterAlgorithm.Hdbscan" MinClusterSize="15"
                   Dataset="@SampleDatasets.Segments()" OnClustered="r => silhouette = r.Silhouette" />
```

### AIKnowledgeGraph3D

Mengekstrak entitas dan relasi dari teks lalu menampilkannya sebagai graph 3D, dalam empat tampilan.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Knowledge graph"` | Judul kartu. |
| `Mode` | `string` | `"Knowledge graph"` | `Knowledge graph`, `Entity relationship`, `Semantic`, atau `Concept map`. |
| `Text` | `string` | teks singkat tentang Borobudur dan Prambanan | Teks sumber ekstraksi graph. |
| `Graph` | `ExtractedGraph?` | `null` | Graph yang ditampilkan sebagai pengganti hasil ekstraksi dari `Text`. |
| `OnEntitySelected` | `EventCallback<GraphEntity>` | | Dipicu saat sebuah entitas diklik. |

```razor
<AIKnowledgeGraph3D Mode="Concept map" Text="@article" OnEntitySelected="e => selected = e.Name" />
```

### AIVectorSpaceViewer

Memproyeksikan embedding dokumen, pertanyaan, dan jawaban ke 3D dengan PCA; sebuah query menampilkan tetangga terdekatnya.

Base: `ThreeDComponentBase`. Memakai embedding model dari provider dan kembali ke hashing embedding offline jika tidak tersedia.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Vector space viewer"` | Judul kartu. |
| `Source` | `IReadOnlyList<AIVectorSpaceViewer.VectorItem>?` | `null` | Item dalam bentuk `(Kind, Text)`. `null` memakai sampel. |
| `TopK` | `int` | `5` | Jumlah tetangga yang ditampilkan untuk sebuah query. |

```razor
<AIVectorSpaceViewer Source="@items" TopK="5" />
```

### Agent

### AgentStudio3D

Ekosistem agent dalam 3D: agent sebagai node dan tool sebagai satelit; ubah agent di panel samping dan jalankan sebuah task untuk melihat alurnya.

Base: `ThreeDComponentBase`. Membutuhkan `AddBlazorAIAgents()`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `OnAgentSaved` | `EventCallback<AgentDefinition>` | | Dipicu saat sebuah agent disimpan. |

```razor
<AgentStudio3D OnAgentSaved="a => Console.WriteLine(a.Name)" />
```

### MultiAgentArena

Agent-agent duduk di arena melingkar dan saling mengirim pesan sebagai paket bercahaya sambil mengerjakan sebuah tujuan.

Base: `ThreeDComponentBase`. Membutuhkan `AddBlazorAIAgents()`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Goal` | `string` | contoh tujuan | Task untuk tim. |
| `Mode` | `OrchestrationMode` | `OrchestrationMode.Sequential` | `Sequential`, `RoundRobin`, atau `Supervisor`. |
| `Team` | `IReadOnlyList<AgentDefinition>` | `MultiAgentArena.DefaultTeam` | Agent sesuai urutan bicara. Default-nya agent riset, perencanaan, coding, dan evaluasi. |
| `OnCompleted` | `EventCallback<IReadOnlyList<AgentMessage>>` | | Dipicu dengan transkrip. |

```razor
<MultiAgentArena Goal="Rancang aplikasi antrean puskesmas" Mode="OrchestrationMode.RoundRobin"
                 OnCompleted="msgs => transcript = msgs" />
```

### AgentMindMap3D

Menampilkan rencana agent untuk sebuah tujuan sebagai reasoning tree, planning graph dengan dependensi, atau execution graph yang disimulasikan.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Goal` | `string` | contoh tujuan | Tujuan yang direncanakan. |
| `View` | `string` | `"Reasoning tree"` | `Reasoning tree`, `Planning graph`, atau `Execution graph`. |
| `StepDelayMs` | `int` | `650` | Jeda antarlangkah saat menyimulasikan eksekusi. |
| `OnStepSelected` | `EventCallback<MindStep>` | | Dipicu saat sebuah langkah diklik. |

```razor
<AgentMindMap3D Goal="Digitalise 120 village cooperatives in NTT" View="Execution graph" StepDelayMs="500" />
```

### Keputusan

![Strategy simulator](../screenshots/3d-strategysimulator3d.jpg)

### DecisionTree3D

Decision tree 3D yang interaktif: ubah kasusnya, lihat kasus itu menelusuri tree, lalu minta AI menjelaskan hasilnya.

Base: `ThreeDComponentBase`. Memakai `DecisionTreeNode` dari Blazor.AI.Decision.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Decision tree"` | Judul kartu. |
| `Subtitle` | `string` | `"Customer churn risk"` | Subjudul kartu. |
| `Tree` | `DecisionTreeNode` | `DecisionTreeNode.ChurnSample()` | Tree-nya. Kondisi memakai bahasa ekspresi Blazor.AI.Decision. |
| `Facts` | `IDictionary<string, object?>` | contoh pelanggan | Fakta input. |
| `OnDecided` | `EventCallback<DecisionTreeNode>` | | Dipicu dengan leaf yang dicapai. |

```razor
<DecisionTree3D Tree="@loanTree" Facts="@applicant" Subtitle="Loan approval"
                OnDecided="leaf => outcome = leaf.Outcome" />
```

### RuleGraph3D

Business rule sebagai graph 3D: fakta mengalir ke rule, rule menulis hasil, rule yang terpicu menyala, dan variabel yang ditulis oleh beberapa rule ditandai sebagai konflik.

Base: `ThreeDComponentBase`. Memakai rule engine Blazor.AI.Decision.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Rule graph"` | Judul kartu. |
| `Rules` | `List<BusinessRule>` | `RuleGraph3DSamples.Rules()` | Rule yang dievaluasi. |
| `Facts` | `IDictionary<string, object?>` | `RuleEditor.SampleFacts()` | Fakta input. |
| `Mode` | `RuleMode` | `RuleMode.AllMatches` | `AllMatches` atau `FirstMatch`. |
| `OnEvaluated` | `EventCallback<RuleEvaluation>` | | Dipicu setelah evaluasi. |

```razor
<RuleGraph3D Rules="@rules" Facts="@order" OnEvaluated="r => fired = r.Fired.Count()" />
```

### StrategySimulator3D

Simulasi Monte-Carlo sebuah KPI terhadap dua tuas, ditampilkan sebagai lanskap 3D dengan titik pilihanmu, titik terbaik, dan menara skenario dengan risiko P5–P95.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Strategy simulator"` | Judul kartu. |
| `Preset` | `string` | `"banking"` | `banking`, `insurance`, `healthcare`, `supply`, atau `retail`. |
| `Model` | `StrategyPreset?` | `null` | Model kustom yang dipakai sebagai pengganti preset. |
| `Trials` | `int` | `160` | Jumlah trial Monte-Carlo per titik grid. |

```razor
<StrategySimulator3D Preset="supply" Trials="200" Language="Bahasa Indonesia" />
```

### Reinforcement learning

### RLTrainingArena

Robot mempelajari grid world 3D dengan Q-learning atau SARSA; training berjalan di .NET dan policy saat ini diputar ulang setiap beberapa episode.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"RL training arena"` | Judul kartu. |
| `World` | `DesignedGridWorld?` | `null` | Grid-nya. `null` memakai maze 9×7 dengan dua jebakan. |
| `Algorithm` | `GridAlgorithm` | `GridAlgorithm.QLearning` | `QLearning` atau `Sarsa`. |
| `Episodes` | `int` | `400` | Jumlah episode training (dibatasi ke 20–2000). |
| `Slip` | `double` | `0` | Probabilitas sebuah langkah tergelincir ke samping. Hanya berlaku untuk grid bawaan. |
| `OnEpisode` | `EventCallback<GridEpisode>` | | Dipicu setelah setiap episode. |

```razor
<RLTrainingArena Algorithm="GridAlgorithm.Sarsa" Episodes="600" Slip="0.1"
                 OnEpisode="e => returns.Add(e.Return)" />
```

### RLPolicyViewer

Menampilkan policy hasil training sebagai kolom nilai dan panah aksi greedy per sel, dengan slider untuk menelusuri snapshot training.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Policy evolution"` | Judul kartu. |
| `World` | `DesignedGridWorld?` | `null` | Grid-nya. `null` memakai maze bawaan. |
| `Algorithm` | `GridAlgorithm` | `GridAlgorithm.QLearning` | `QLearning` atau `Sarsa`. |
| `Episodes` | `int` | `360` | Jumlah episode training (dibatasi ke 40–3000). |
| `Snapshots` | `int` | `24` | Jumlah snapshot yang disimpan selama training (dibatasi ke 4–60). |

```razor
<RLPolicyViewer Episodes="500" Snapshots="30" Algorithm="GridAlgorithm.QLearning" />
```

### RLBattleArena

Pemburu dan pelari belajar saling melawan dalam game pursuit–evasion, dengan papan skor live dan replay pertandingan.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"RL battle arena"` | Judul kartu. |
| `HunterPolicy` | `BattlePolicy` | `BattlePolicy.Learning` | `Learning`, `Heuristic`, atau `Random`. |
| `RunnerPolicy` | `BattlePolicy` | `BattlePolicy.Learning` | `Learning`, `Heuristic`, atau `Random`. |
| `Matches` | `int` | `4000` | Jumlah pertandingan training (dibatasi ke 200–20000). |

```razor
<RLBattleArena HunterPolicy="BattlePolicy.Learning" RunnerPolicy="BattlePolicy.Heuristic" Matches="6000" />
```

### RLEvolutionViewer

Mengevolusikan controller cart-pole dan menampilkan generasi sebagai cincin bertumpuk, fitness sebagai ukuran dan warna, garis keturunan sang juara, serta sang juara yang sedang menyeimbangkan tiang secara live.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Evolution viewer"` | Judul kartu. |
| `GenerationCount` | `int` | `18` | Jumlah generasi yang dievolusikan (dibatasi ke 2–60). |
| `Population` | `int` | `24` | Jumlah controller per generasi (dibatasi ke 8–60). |
| `Mutation` | `double` | `0.35` | Sigma mutasi (dibatasi ke 0.01–2). |

```razor
<RLEvolutionViewer GenerationCount="25" Population="32" Mutation="0.3" />
```

### ResourceOptimizationArena

Policy RL mengalokasikan unit ke zona (menara menunjukkan permintaan yang diharapkan, kubus bertumpuk menunjukkan unit) dan dibandingkan dengan baseline.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Resource optimisation arena"` | Judul kartu. |
| `Scenario` | `AllocationScenario?` | `null` | Skenario kustom yang dipakai sebagai pengganti preset bawaan. |
| `TrainingDays` | `int` | `3000` | Jumlah hari training yang disimulasikan. |

```razor
<ResourceOptimizationArena Scenario="@depots" TrainingDays="4000" />
```

### Digital twin

![Smart city](../screenshots/3d-smartcity3d.jpg)

Setiap twin menjalankan `TwinSimulator` dengan timer, mewarnai scene dari pembacaan live, dan menyertakan `TwinCopilot`. Base-nya `TwinComponentBase`, jadi twin juga menerima `UpdatesPerSecond` dan `AutoStart`.

### SmartFactory3D

Pabrik dengan lini produksi dan sel robot, OEE live, predictive maintenance (remaining useful life), dan copilot AI.

Base: `TwinComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Plant Cikarang · Assembly"` | Judul kartu. |
| `Lines` | `int` | `2` | Jumlah lini produksi (1–3). |
| `StationsPerLine` | `int` | `3` | Jumlah sel robot per lini (2–4). |
| `IdealCycleSeconds` | `double` | `42` | Cycle time ideal per stasiun dalam detik, baseline performa OEE. |

```razor
<SmartFactory3D Title="Plant Karawang · Line A/B" Lines="2" StationsPerLine="3"
                IdealCycleSeconds="40" Language="Bahasa Indonesia" />
```

### SmartBuilding3D

Gedung dengan lantai dan ruangan yang diwarnai menurut energi, suhu, atau okupansi, titik-titik orang, peringatan keamanan, dan copilot AI.

Base: `TwinComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Menara Kawung · HQ"` | Judul kartu. |
| `Floors` | `int` | `4` | Jumlah lantai (1–8). |
| `RoomArea` | `double` | `120` | Luas ruangan dalam meter persegi, dipakai untuk intensitas energi. |

```razor
<SmartBuilding3D Floors="5" RoomArea="140" UpdatesPerSecond="1" />
```

### SmartFarm3D

Kebun dengan zona irigasi, petak kelembapan tanah, jagung yang tumbuh, deteksi penyakit, prediksi hasil panen, dan agronom AI.

Base: `TwinComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Kebun Jagung Sleman"` | Judul kartu. |
| `SeasonDays` | `int` | `110` | Panjang musim tanam dalam hari. |
| `PotentialYield` | `double` | `9.5` | Hasil panen yang bisa dicapai dalam t/ha pada kondisi ideal. |

```razor
<SmartFarm3D SeasonDays="110" PotentialYield="9.5" />
```

### SmartAquaculture3D

Keramba jaring dengan ikan yang berenang, kualitas air, prediksi pertumbuhan, dan optimasi pemberian pakan.

Base: `TwinComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Waduk Jatiluhur"` | Judul kartu. |
| `FishPerCage` | `int` | `6000` | Jumlah ikan yang ditebar per keramba. |
| `HarvestWeight` | `double` | `500` | Berat panen dalam gram. |
| `VisibleFish` | `int` | `9` | Jumlah model ikan yang digambar per keramba, sebagai sampel visual dari stok. |

```razor
<SmartAquaculture3D Title="Keramba Waduk Jatiluhur" FishPerCage="6000" HarvestWeight="500" />
```

### SmartCity3D

Kota dengan tinggi gedung berbasis data, lalu lintas per jalan, mobil yang bergerak, kondisi jembatan dan utilitas, gelembung kualitas udara, dan copilot AI.

Base: `TwinComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Bandung Smart City"` | Judul kartu. |
| `BlockValues` | `IReadOnlyList<double>?` | `null` | Satu nilai per blok kota, urut per baris (misalnya kepadatan penduduk), yang menentukan tinggi gedung. Kota ini punya 9 blok; kurang dari 9 nilai akan memakai sampel. |

```razor
<SmartCity3D BlockValues="@densityIndex" />
```

### TwinCopilot

Menjawab pertanyaan tentang digital twin yang sedang berjalan dari snapshot pembacaan, tren, dan event-nya; saat offline, diagnosis lokal yang menjawab. Twin di atas sudah menyertakannya; pakai langsung dengan `TwinSimulator` milikmu sendiri.

Base: `AiComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Simulator` | `TwinSimulator` | | Wajib. Simulator yang dibaca. |
| `Role` | `string` | `"an operations engineer for this digital twin"` | Peran copilot. |
| `Context` | `Func<string>?` | `null` | Fakta tambahan yang ditambahkan ke snapshot, misalnya KPI yang dihitung oleh component host. |
| `Placeholder` | `string` | `"Ask about the live readings…"` | Placeholder input. |
| `Suggestions` | `IReadOnlyList<string>` | `[]` | Pertanyaan pembuka. |
| `Language` | `string?` | `null` | Bahasa jawaban. |

```razor
<TwinCopilot Simulator="@sim" Role="a plant reliability engineer" Context="@Kpis" />
```

### Kesehatan & bioteknologi

![Molecule explorer](../screenshots/3d-moleculeexplorer3d.jpg)

### HumanBody3D

Viewer anatomi dengan tubuh transparan serta lapisan organ dan kerangka; klik sebuah organ untuk memfokuskan kamera dan mempelajarinya.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Audience` | `string` | `"a high-school student"` | Untuk siapa penjelasan ditulis. |
| `OnOrganSelected` | `EventCallback<HumanBody3D.Organ>` | | Dipicu saat sebuah organ dipilih. |

```razor
<HumanBody3D Audience="a nursing student" Language="Bahasa Indonesia" OnOrganSelected="o => selected = o.Name" />
```

### MoleculeExplorer3D

Membangun molekul 3D dari SMILES (conformer embedding) dan menampilkannya sebagai ball-and-stick, space-filling, atau wire dengan warna CPK, descriptor, dan penjelasan AI.

Base: `ThreeDComponentBase`. Memakai Biotech.Net melalui Blazor.AI.Bio.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"3D molecule explorer"` | Judul kartu. |
| `Smiles` | `string?` | `BioSamples.Molecules[1].Smiles` (kafein) | Molekul dalam format SMILES. |

```razor
<MoleculeExplorer3D Smiles="CC(=O)Oc1ccccc1C(=O)O" />
```

### ProteinViewer3D

Menampilkan struktur PDB sebagai tube jejak Cα yang diwarnai menurut chain, pelangi N→C, atau struktur sekunder; residu menampilkan tooltip saat di-hover.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Protein viewer"` | Judul kartu. |
| `Pdb` | `string?` | `null` | Teks PDB. `null` memakai sampel sintetis. |
| `MaxMarkers` | `int` | `350` | Jumlah maksimum residu yang digambar sebagai penanda yang bisa di-pick. |

```razor
<ProteinViewer3D Pdb="@pdbText" MaxMarkers="300" />
```

### GeneNetwork3D

Jaringan ko-ekspresi gen: korelasi Pearson dari log ekspresi untuk gen-gen yang paling bervariasi, ditata dalam 3D dan diwarnai menurut fold change.

Base: `ThreeDComponentBase`. Memakai Blazor.AI.Bio.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Gene network explorer"` | Judul kartu. |
| `Counts` | `Dataset?` | `null` | Count dalam bentuk gen × sampel dengan anotasi `condition`. `null` memakai eksperimen stres sintetis. |
| `Reference` | `string` | `"control"` | Kondisi referensi. |
| `Treatment` | `string` | `"treated"` | Kondisi perlakuan. |

```razor
<GeneNetwork3D Counts="@counts" Reference="control" Treatment="treated" />
```

### MedicalTrainingSimulator

Pelatihan resusitasi dengan pasien 3D, monitor tanda vital yang digerakkan state machine BLS, penilaian, dan debrief AI.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Resuscitation trainer"` | Judul kartu. |
| `Briefing` | `string` | skenario pasien yang kolaps | Teks skenario yang ditampilkan di awal. |
| `Audience` | `string` | `"a nursing student"` | Untuk siapa debrief ditulis. |
| `OnFinished` | `EventCallback<CprScenario>` | | Dipicu saat skenario selesai, beserta skor dan log-nya. |

```razor
<MedicalTrainingSimulator Audience="a first-year medical student" OnFinished="s => SaveScore(s.Score, s.Log)" />
```

### Rekayasa

![CAD copilot](../screenshots/3d-cadcopilot3d.jpg)

### CADCopilot3D

Mengubah deskripsi menjadi part parametrik: AI (atau parser lokal) menulis daftar fitur, kamu mengubah dimensinya, dan hasilnya bisa diekspor ke JSON.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `OnPartChanged` | `EventCallback<CadPart>` | | Dipicu saat part berubah. |

```razor
<CADCopilot3D OnPartChanged="part => Save(part.ToJson())" />
```

### BIMAssistant3D

Ajukan pertanyaan tentang model bangunan: AI memetakan pertanyaan ke sebuah query, .NET menghitung jawabannya dari elemen-elemen bangunan dan menyorotnya.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Elements` | `IReadOnlyList<BimElement>?` | `null` | Elemen bangunan. `null` memakai sampel dua lantai dengan benturan duct yang disengaja. |

```razor
<BIMAssistant3D Elements="@myElements" Language="Bahasa Indonesia" />
```

### EngineeringSimulation3D

Analisis CFD, FEM, termal, dan struktural yang dihitung di C# dan ditampilkan dalam 3D, dengan penjelasan hasil oleh AI.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `InitialMode` | `string` | `"CFD"` | Mode yang dibuka pertama: `CFD`, `FEM`, `Thermal`, atau `Structural`. |

```razor
<EngineeringSimulation3D InitialMode="FEM" />
```

### MeshOptimizationStudio

Decimation mesh yang ditampilkan berdampingan (asli dan teroptimasi), dengan anggaran segitiga, pengukuran LOD, dan rekomendasi AI.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Url` | `string?` | `null` | Model glTF/GLB yang dibuka. `null` memakai `ThreeDAssets.Stupa`. |

```razor
<MeshOptimizationStudio Url="@ThreeDAssets.Stupa" />
```

### Pendidikan

### AIClassroom3D

Kelas virtual: AI menulis materi pelajaran dan kuis, lalu robot guru menyampaikan setiap poin di papan tulis dengan lip sync.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `SpeechLanguage` | `string` | `"en-US"` | Bahasa BCP-47 untuk suara guru. |

```razor
<AIClassroom3D SpeechLanguage="id-ID" Language="Bahasa Indonesia" />
```

### SolarSystemExplorer

Matahari dan delapan planet dengan orbit pada kecepatan relatif, jarak yang mudah dibaca atau sesuai rasio sebenarnya, fakta, serta tanya jawab AI per planet.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Focus` | `string?` | `null` | Planet yang dipilih pertama kali. |

```razor
<SolarSystemExplorer Focus="Jupiter" Height="560px" />
```

### HistoryExplorer3D

Situs bersejarah dalam 3D (Borobudur, Prambanan) dengan hotspot cerita, timeline yang membangun ulang situs per era, dan narator AI.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `InitialSite` | `string` | `"Borobudur"` | Situs yang dibuka pertama: `Borobudur` atau `Prambanan`. |

```razor
<HistoryExplorer3D InitialSite="Prambanan" Language="Bahasa Indonesia" />
```

### ScienceLab3D

Lab virtual dengan bandul (RK4), gerak proyektil, dan rangkaian hukum Ohm, yang dihitung di C#, dianimasikan dalam 3D, diplot, dan dijelaskan.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `InitialExperiment` | `string` | `"Pendulum"` | Eksperimen yang dibuka pertama: `Pendulum`, `Projectile`, atau `Ohm's law`. |

```razor
<ScienceLab3D InitialExperiment="Pendulum" />
```

### Gaming

![World builder](../screenshots/3d-aiworldbuilder.jpg)

### NPCStudio

Membuat NPC dengan kepribadian, latar belakang, dialog, dan statistik, menampilkan preview 3D dengan emote, dan memungkinkanmu chat dengan NPC sesuai karakternya.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `OnGenerated` | `EventCallback<NPCStudio.Npc>` | | Dipicu dengan setiap NPC yang dibuat. |

```razor
<NPCStudio OnGenerated="npc => SaveNpc(npc)" />
```

### AIQuestGenerator

Membuat rangkaian quest (langkah, objektif, hadiah, percabangan) dari sebuah tema dan menatanya sebagai waypoint di peta 3D.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `OnGenerated` | `EventCallback<AIQuestGenerator.Quest>` | | Dipicu dengan setiap quest yang dibuat. |

```razor
<AIQuestGenerator OnGenerated="quest => questLog.Add(quest)" />
```

### AIWorldBuilder

Mengubah prompt menjadi rencana dunia (bioma, landmark, permukiman) lalu membangun dunia 3D prosedural dengan hutan, desa, dan candi.

Base: `ThreeDComponentBase`. Tidak ada parameter lain.

```razor
<AIWorldBuilder Height="600px" />
```

### AIDungeonMaster

Game master AI di atas dungeon meja 3D: dadu dilempar secara lokal, rule menentukan hasil, dan model yang bernarasi.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Party` | `IReadOnlyList<string>` | `["Arjuna the archer", "Sinta the healer", "Bima the warrior"]` | Anggota party yang ditampilkan sebagai token. |

```razor
<AIDungeonMaster Party="@(["Arjuna the archer", "Sinta the healer"])" />
```

### ProceduralWorldStudio

Terrain prosedural ber-seed (noise fBm, island falloff, punggungan, erosi termal); AI mengubah deskripsi menjadi pengaturan generator.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Seed` | `int` | `1337` | Seed acak awal. |
| `OnGenerated` | `EventCallback<Terrain>` | | Dipicu dengan setiap terrain yang dibuat. |

```razor
<ProceduralWorldStudio Seed="2026" OnGenerated="terrain => heightmap = terrain" />
```

### Enterprise

![Executive analytics sphere](../screenshots/3d-executiveanalyticssphere.jpg)

### DigitalTwinPlatform

Digital twin multi-lokasi (situs, area, aset) dengan kondisi dan suhu live, alarm, remaining useful life, dan copilot pemeliharaan AI.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `IntervalMs` | `int` | `1500` | Interval pembaruan dalam milidetik (minimum 500). |

```razor
<DigitalTwinPlatform IntervalMs="1500" Height="560px" />
```

### EnterpriseKnowledgeGraph3D

Knowledge graph perusahaan berisi tim, sistem, dataset, dokumen, dan orang, dengan pencarian, filter domain, serta tanya jawab atas graph.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Nodes` | `IReadOnlyList<EnterpriseKnowledgeGraph3D.EnterpriseNode>` | contoh perusahaan | Node dalam bentuk `(Name, Type, Domain)`. |
| `Edges` | `IReadOnlyList<EnterpriseKnowledgeGraph3D.EnterpriseLink>` | contoh perusahaan | Link dalam bentuk `(From, Relation, To)`. |

```razor
<EnterpriseKnowledgeGraph3D Nodes="@nodes" Edges="@edges" />
```

### EnterpriseAgentOperationsCenter

Armada agent AI yang ditampilkan sebagai menara (tinggi adalah throughput, warna adalah error rate, cincin adalah SLO latensi), dengan insiden dan ringkasan operasional dari AI.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `LatencySloMs` | `double` | `2500` | Service-level objective latensi dalam milidetik. |

```razor
<EnterpriseAgentOperationsCenter LatencySloMs="2000" />
```

### ExecutiveAnalyticsSphere

KPI eksekutif di atas sebuah globe: posisi adalah domain, ukuran adalah pencapaian, warna menunjukkan sesuai atau meleset dari target, dan denyut menandai anomali.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Company pulse"` | Judul kartu. |
| `Kpis` | `IReadOnlyList<LiveMetric>?` | `null` | KPI milikmu sendiri. `null` memakai perusahaan simulasi. |
| `IntervalMs` | `int` | `2000` | Interval pembaruan dalam milidetik (minimum 500). |

```razor
<ExecutiveAnalyticsSphere Title="Board pulse" Kpis="@kpis" IntervalMs="5000" />
```

### MultiAgentCommandCenter

Pusat kendali untuk tim agent: memecah misi menjadi task, membagikannya ke agent spesialis, dan menampilkan eksekusinya dalam 3D.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Agents` | `IReadOnlyList<string>` | `["Researcher", "Planner", "Analyst", "Writer", "Reviewer"]` | Peran spesialis dalam tim. |

```razor
<MultiAgentCommandCenter Language="Bahasa Indonesia" />
```

### AutonomousOperationsDashboard

Loop tertutup sense, analyse, decide, dan act: keputusan dengan confidence sama dengan atau di atas threshold dieksekusi otomatis, sisanya menunggu persetujuan manusia.

Base: `ThreeDComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Threshold` | `double` | `0.85` | Confidence minimum agar sebuah keputusan dieksekusi otomatis. |
| `IntervalMs` | `int` | `3500` | Durasi satu siklus loop dalam milidetik (minimum 1000). |

```razor
<AutonomousOperationsDashboard Threshold="0.9" IntervalMs="3000" />
```

---

## Blazor.AI.MediaPipe

Paket: `Blazor.AI.MediaPipe` · Namespace: `Blazor.AI.MediaPipe`

```csharp
builder.Services.AddBlazorAIMediaPipe(o => o.PreloadModels = true);
```

```html
<link rel="stylesheet" href="_content/Blazor.AI.MediaPipe/blazor-ai-vision.css" />
```

`AddBlazorAIMediaPipe()` mendaftarkan `VisionModelManager`, `FaceGallery` dan `VisionEventStream` per pengguna, `IFaceEmbedder` default, preloader model, serta menaikkan batas ukuran pesan hub Blazor Server agar frame JPEG muat. Component-component ini dibangun di atas `AddBlazorAIComponents()`. Pakai render mode Interactive Server: inferensi berjalan di server dengan MediaPipe.NET di atas ONNX Runtime, yang tidak bisa berjalan di WebAssembly browser. Component yang menampilkan tampilan 3D (`FaceMeshViewer`, component avatar dan motion, serta grup 3D) juga membutuhkan `_content/Blazor.AI.ThreeD/blazor-ai-3d.css`. Lihat [Computer vision](vision.md) untuk alur frame dan cara membuat component vision sendiri.

```razor
@using Blazor.AI.MediaPipe
```

`MediaPipeVisionOptions`, diatur di callback `AddBlazorAIMediaPipe`:

| Opsi | Tipe | Default | Fungsi |
|---|---|---|---|
| `Provider` | `ExecutionProvider` | `ExecutionProvider.Auto` | Execution provider ONNX Runtime: `Auto`, `Cpu`, `Cuda`, `DirectML`, atau `CoreML`. |
| `ModelDirectory` | `string?` | `null` | Folder berisi model yang sudah diunduh untuk lokasi offline dan edge. `null` memakai cache MediaPipeNet. |
| `AllowModelDownload` | `bool` | `true` | Mengunduh model yang belum ada saat pertama dipakai. Matikan untuk lokasi air-gapped. |
| `PreloadModels` | `bool` | `false` | Mengunduh model-model umum saat startup, bukan saat pertama dipakai. |
| `MaxFrameMessageBytes` | `long` | `2 * 1024 * 1024` | Ukuran pesan frame terbesar yang diterima hub. Frame biasanya 15–60 KB. |

### Parameter umum vision

Sebagian besar component mewarisi `VisionComponentBase`, yang mewarisi `AiComponentBase`. Component tersebut menerima [parameter umum](#parameter-umum) (`Provider`, `Class`, `OnError`, atribut yang tidak cocok) ditambah:

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Height` | `string` | `"420px"` | Tinggi CSS tampilan kamera. |
| `Samples` | `IReadOnlyList<VisionSample>?` | `null` | Gambar contoh yang ditawarkan saat kamera tidak dipakai. `null` memakai contoh bawaan component. |
| `StartWithCamera` | `bool` | `false` | Memulai webcam, bukan contoh pertama. |
| `Fps` | `double` | `12` | Frame per detik yang diminta dari sumber live. |
| `Language` | `string?` | `null` | Bahasa jawaban AI. `null` mengikuti default model. |

Setiap frame diproses di luar thread circuit; sumber live memakai mode VIDEO MediaPipe (tracking dan smoothing), sedangkan gambar diam memakai mode IMAGE. Deteksi, landmark, sudut, dan hitungan dijalankan di .NET; model hanya menjelaskan fakta hasil perhitungan, dengan frame dilampirkan untuk model yang mendukung vision dan fakta saja untuk model teks.

Component solusi (kesehatan, agritech, pendidikan, industri, dan sebagian analitik) mewarisi `SolutionComponentBase`, yang mewarisi `VisionComponentBase` dan menambahkan:

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Events` | `VisionEventStream?` | `null` | Stream tempat component menerbitkan event. `null` memakai stream per pengguna yang didaftarkan oleh `AddBlazorAIMediaPipe()`. |
| `OnEvent` | `EventCallback<VisionEvent>` | | Dipicu untuk setiap event yang diterbitkan component. |
| `SourceName` | `string?` | `null` | Nama sumber event. `null` memakai nama tipe component. |

Base ini juga menyediakan `CameraStats` (`VisionStats?`) untuk `RealtimeMetrics`. Event peringatan dan alert ditulis ke audit trail guardrail.

`EventTimeline` dan `VisionDashboard` mewarisi `StreamComponentBase`, yang mewarisi `AiComponentBase` dan menambahkan:

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Events` | `VisionEventStream?` | `null` | Stream yang ditampilkan. `null` memakai stream per pengguna yang didaftarkan oleh `AddBlazorAIMediaPipe()`. |
| `RefreshMs` | `int` | `400` | Jeda minimum dalam milidetik antar-render ulang selama event berdatangan. |

Berikan instance `VisionEventStream` yang sama ke component penerbit dan component tampilan agar keduanya terhubung dalam satu halaman.

### Tipe pembantu

- `VisionSamples`: foto contoh yang dibundel di paket (`Face`, `ThumbsUp`, `Yoga`, `Street`, `Food`, `OpenPalms`, `DeskPosture`, `Fall`, `Construction`, `Warehouse`, `Shelf`, `LeafDisease`, `Fruit`, `Livestock`, `Fish`) beserta kelompoknya (`People`, `Hands`, `Bodies`, `Scenes`, `Agritech`, `All`). `VisionSample` adalah record `(Name, Url)`.
- `VisionMath`: sudut sendi 2-D (`Angle`) dan dari world landmark 3-D (`Angle3`), `Distance`, `ExtendedFingers`, `CountFingers`, `EyeAspectRatio`, serta perkiraan kasar `HeadPose` (yaw, pitch).
- `Overlay`: menyusun bagian `VisionOverlay` dari hasil MediaPipe.NET (`Hand`, `Pose`, `Face`, `Box`, `Mask`, `CategoryTint`, `Label`, `Bounds`) dan menyimpan warna tema (`Nila`, `Soga`, `Tarum`, `Danger`, `Warning`, `White`). Koordinat overlay dinormalisasi ke 0–1.
- `FaceGallery`: penyimpanan wajah terdaftar per pengguna (scoped) dengan `Enroll`, `Remove`, `Match` (cosine similarity terbaik), dan event `Changed`. Dipakai oleh `FaceAttendance`.
- `IFaceEmbedder`: mengubah wajah yang terdeteksi menjadi vektor identitas. `MeshFaceEmbedder` default meng-embed potongan wajah yang diluruskan berdasarkan posisi mata dengan MobileNet; kualitasnya setara demo, bukan setara biometrik. Daftarkan embedder sendiri (misalnya model ArcFace ONNX) untuk menggantikannya.
- `VisionModelManager`: katalog model (`CommonModels`, `AllModels`), `FindLocal`, `EnsureAsync` untuk mengunduh lebih dulu dengan progres, dan `AvailableProviders`.
- `VisionEventStream`: stream `VisionEvent` di memori yang thread-safe (`Publish`, `Snapshot`, `Clear`, event `Published`, `Capacity` 2000). `VisionEventStream.Shared` adalah instance tingkat proses untuk demo.

### Inti

![Vision core components](../screenshots/vision-core.jpg)

### CameraViewer

Kamera universal yang menjadi dasar semua component vision: webcam atau kamera ponsel, gambar contoh atau unggahan, file video, dan stream HTTP, lengkap dengan overlay, mirror, snapshot, dan perekaman.

Tanpa panggilan AI. Tidak mewarisi base class.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `OnFrame` | `Func<VisionFrame, Task<VisionOverlay?>>?` | `null` | Dipanggil untuk setiap frame; mengembalikan overlay yang digambar, atau `null`. Berjalan di circuit, jadi pindahkan pekerjaan berat ke thread lain. |
| `OnSourceChanged` | `EventCallback<VisionSourceKind>` | | Dipicu saat sumber berganti. Reset state tracking di sini. |
| `Samples` | `IReadOnlyList<VisionSample>` | `VisionSamples.People` | Gambar contoh di toolbar. |
| `InitialImage` | `string?` | `null` | Gambar pertama yang ditampilkan. `null` memakai contoh pertama. |
| `StartWithCamera` | `bool` | `false` | Langsung memulai webcam (sebagian browser butuh gestur pengguna). |
| `AllowCamera` | `bool` | `true` | Menampilkan tombol kamera. |
| `AllowStreamUrl` | `bool` | `true` | Menampilkan tombol URL untuk stream, video, dan gambar. |
| `ShowToolbar` | `bool` | `true` | Menampilkan toolbar sumber. |
| `FacingMode` | `string` | `"user"` | Facing mode kamera untuk `getUserMedia`, misalnya `"environment"` untuk kamera belakang. |
| `Fps` | `double` | `12` | Frame per detik yang diminta dari sumber live. Laju sebenarnya dibatasi waktu pemrosesan. |
| `MaxWidth` | `int` | `640` | Frame diskalakan ke lebar ini sebelum diunggah. |
| `Height` | `string` | `"420px"` | Tinggi CSS. |
| `Class` | `string?` | `null` | Kelas CSS tambahan untuk elemen root. |
| `ChildContent` | `RenderFragment?` | `null` | Overlay HTML (HUD) yang digambar di atas gambar. |

Properti: `Stats` (`VisionStats`: fps, latensi, jumlah frame, lebar, tinggi), `Source` (`VisionSourceKind`), dan `LastJpeg` (frame terakhir yang diterima). Method:

- `StartAsync(VisionSourceKind source, string? url)`: berganti sumber (`Image`, `Camera`, `Video`, atau `Stream`); mengembalikan `null` jika berhasil, atau sebuah pesan.
- `UseSampleAsync(string? url)`: menampilkan gambar contoh.
- `ReprocessAsync()`: memproses ulang gambar diam saat ini setelah pengaturan berubah.
- `SnapshotAsync(bool withOverlay = true)`: data URL JPEG dari gambar, secara default beserta overlay-nya.
- `StartRecordingAsync(int fps = 24)` dan `StopRecordingAsync()`: merekam gambar beserta overlay-nya; saat dihentikan mengembalikan object URL klip WebM.
- `DownloadAsync(string url, string fileName)`: menyimpan snapshot atau klip di browser.
- `SetOverlayAsync(VisionOverlay? overlay)`: menggambar overlay secara langsung (untuk replay), tanpa melalui `OnFrame`.
- `StopAsync()`: menghentikan sumber saat ini.

```razor
@inherits VisionComponentBase

<CameraViewer @ref="Camera" OnFrame="HandleFrameAsync" OnSourceChanged="HandleSourceChanged" Samples="@EffectiveSamples" />
```

### VisionCanvas

Menampilkan gambar diam dengan overlay yang kamu sediakan, untuk aplikasi yang menjalankan inferensinya sendiri. Tanpa kamera dan tanpa pemrosesan.

Tanpa panggilan AI. Tidak mewarisi base class.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `ImageUrl` | `string?` | `null` | URL gambar: relatif, absolut dengan CORS, atau data URL. |
| `ImageBytes` | `byte[]?` | `null` | Byte gambar ter-encode (JPEG atau PNG), dipakai saat `ImageUrl` bernilai `null`. |
| `ContentType` | `string` | `"image/jpeg"` | Content type `ImageBytes`. |
| `Overlay` | `VisionOverlay?` | `null` | Apa yang digambar di atas gambar, dalam koordinat ternormalisasi. |
| `Height` | `string` | `"360px"` | Tinggi CSS. |
| `Class` | `string?` | `null` | Kelas CSS tambahan. |

```razor
<VisionCanvas ImageBytes="@jpeg" Overlay="@overlay" Height="360px" />
```

### VisionRecorder

Merekam klip gambar beserta overlay-nya, mengambil screenshot, dan menyimpan snapshot bukti secara otomatis saat pemicu terpenuhi (misalnya saat jumlah wajah berubah).

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Vision recorder"` | Judul kartu. |
| `SnapshotCooldownSeconds` | `double` | `3` | Jeda minimum dalam detik antar-snapshot otomatis. |
| `OnEvidence` | `EventCallback<EvidenceSnapshot>` | | Dipicu untuk setiap snapshot bukti (data URL, waktu, dan alasan). |

```razor
<VisionRecorder SnapshotCooldownSeconds="5" OnEvidence="Store" />
```

### ModelManagerPanel

Katalog model untuk deployment offline dan edge: model yang sudah ada di disk, ukuran dan lisensinya, serta unduhan lebih dulu dengan progres.

Tanpa panggilan AI. Tidak mewarisi base class. Memakai `VisionModelManager`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Vision models"` | Judul kartu. |
| `Class` | `string?` | `null` | Kelas CSS tambahan. |

```razor
<ModelManagerPanel />
```

### Wajah

![Face mesh](../screenshots/vision-facemesh.jpg)

### FaceDetectionPanel

Deteksi wajah real-time: kotak, enam keypoint, dan confidence dengan model short-range atau full-range, serta privacy blur opsional.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Face detection"` | Judul kartu. |
| `PrivacyBlur` | `bool` | `false` | Memulai dengan wajah di-blur pada gambar. |
| `OnCountChanged` | `EventCallback<int>` | | Dipicu saat jumlah wajah berubah. |

```razor
<FaceDetectionPanel PrivacyBlur="true" OnCountChanged="n => visitors = n" />
```

### FaceLandmarkViewer

Face mesh 478 titik dengan kontur, pose kepala, tingkat keterbukaan mata, dan blendshape terkuat dari 52 blendshape.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Face landmarks"` | Judul kartu. |
| `OnResult` | `Action<FaceLandmarkResult>?` | `null` | Dipanggil dengan hasil setiap frame, di thread pemrosesan. |

```razor
<FaceLandmarkViewer StartWithCamera="true" OnResult="r => faces = r.Faces.Count" />
```

### FaceMeshViewer

Face mesh 478 titik yang di-render dalam 3D dengan Three.js, diwarnai berdasarkan kedalaman, di samping kamera.

Base: `VisionComponentBase`. Membutuhkan stylesheet Blazor.AI.ThreeD.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"3D face mesh"` | Judul kartu. |
| `SceneRate` | `double` | `5` | Jumlah maksimum pembaruan scene 3D per detik (kamera sendiri berjalan lebih cepat). |

```razor
<FaceMeshViewer SceneRate="8" StartWithCamera="true" />
```

### FaceBlendshapeAnalyzer

Analisis ekspresi: senyum, marah, sedih, terkejut, dan netral dari 52 blendshape wajah, secara live dan sebagai tren.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Expression analyzer"` | Judul kartu. |
| `HistoryLength` | `int` | `60` | Jumlah sampel yang disimpan tren. |
| `OnExpression` | `Action<FaceExpression>?` | `null` | Dipanggil dengan ekspresi setiap frame, di thread pemrosesan. |

```razor
<FaceBlendshapeAnalyzer HistoryLength="90" OnExpression="e => mood = e.Dominant" />
```

### FaceAttendance

Presensi: daftarkan orang dari kamera, lalu wajah yang dikenali dicatat hadir sekali per jendela waktu dan bisa diekspor sebagai CSV.

Base: `VisionComponentBase`. Memakai `FaceGallery` dan `IFaceEmbedder`, yang didaftarkan oleh `AddBlazorAIMediaPipe()`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Face attendance"` | Judul kartu. |
| `Threshold` | `double` | `0.82` | Cosine similarity yang dibutuhkan agar dianggap orang yang sama. |
| `DeduplicationWindow` | `TimeSpan` | `TimeSpan.FromMinutes(30)` | Seseorang tidak dicatat hadir lagi dalam jendela waktu ini. |
| `OnCheckIn` | `EventCallback<CheckIn>` | | Dipicu untuk setiap check-in (nama, waktu, similarity). |

```razor
<FaceAttendance Threshold="0.85" DeduplicationWindow="@TimeSpan.FromHours(4)" OnCheckIn="Save" />
```

### FaceVerification

Verifikasi wajah 1:1: ambil wajah referensi dan wajah uji, bandingkan keduanya, dan pakai kedipan sebagai petunjuk liveness.

Base: `VisionComponentBase`. Memakai `IFaceEmbedder`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Face verification"` | Judul kartu. |
| `Threshold` | `double` | `0.82` | Cosine similarity yang dibutuhkan agar dianggap cocok. |
| `OnVerified` | `EventCallback<(double Similarity, bool Matched, int Blinks)>` | | Dipicu setelah setiap verifikasi. |

```razor
<FaceVerification Threshold="0.86" OnVerified="r => approved = r.Matched && r.Blinks > 0" />
```

### FaceAnalyticsDashboard

Analitik audiens: jumlah wajah dari waktu ke waktu, tren emosi, perhatian dari pose kepala, dan slot untuk estimator usia/gender milikmu sendiri.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Face analytics"` | Judul kartu. |
| `Estimator` | `IFaceAttributeEstimator?` | `null` | Estimator usia/gender opsional. Tidak ada yang dibundel secara default. |
| `AttentionDegrees` | `double` | `22` | Wajah dianggap memperhatikan saat yaw dan pitch sama-sama berada dalam rentang derajat ini. |
| `MaxFaces` | `int` | `6` | Jumlah maksimum wajah yang dilacak per frame (dibatasi ke 1–10). |
| `HistoryLength` | `int` | `120` | Jumlah sampel yang disimpan tren. |

```razor
<FaceAnalyticsDashboard Estimator="@estimator" AttentionDegrees="20" Language="Bahasa Indonesia" />
```

### Tangan

![Gesture recognizer](../screenshots/vision-gestures.jpg)

### HandTracker

Tracking tangan 21 titik dengan handedness, sorotan ujung jari, status jari, dan ukuran dalam sentimeter dari world landmark.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Hand tracker"` | Judul kartu. |
| `MaxHands` | `int` | `2` | Jumlah maksimum tangan yang dilacak. |
| `OnResult` | `Action<HandLandmarkResult>?` | `null` | Dipanggil dengan tangan di setiap frame, di thread pemrosesan. |

```razor
<HandTracker MaxHands="2" OnResult="r => hands = r.Hands.Count" />
```

### GestureRecognizerPanel

Pengenalan gestur (jempol ke atas dan ke bawah, victory, menunjuk, telapak terbuka, kepalan, I love you) ditambah gestur kustom yang kamu ajarkan lewat contoh.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Gesture recognizer"` | Judul kartu. |
| `MinScore` | `float` | `0.55f` | Confidence minimum agar sebuah gestur dihitung. |
| `OnGesture` | `EventCallback<string>` | | Dipicu saat gestur yang dikenali berubah, dengan nama bawaan seperti `Thumb_Up` atau nama kustom. |

```razor
<GestureRecognizerPanel MinScore="0.6f" OnGesture="HandleGesture" />
```

### FingerCounter

Menghitung jari yang terbuka pada satu atau dua tangan secara real-time.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Finger counter"` | Judul kartu. |
| `OnCount` | `EventCallback<int>` | | Dipicu saat total berubah. |

```razor
<FingerCounter OnCount="n => answer = n" />
```

### HandDrawingCanvas

Menggambar di udara: tunjuk dengan jari telunjuk untuk menggambar, kepalkan tangan atau cubit untuk mengangkat pena, tahan telapak terbuka untuk menghapus. Bisa diekspor ke SVG (`ToSvg()`) dan AI bisa menebak gambarnya.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Air drawing"` | Judul kartu. |

```razor
<HandDrawingCanvas @ref="canvas" StartWithCamera="true" />
```

### SignLanguageAssistant

Bentuk tangan statis menjadi huruf (sebagian huruf bawaan ditambah isyarat yang kamu ajarkan); AI merangkai huruf-huruf itu menjadi kalimat.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Sign language assistant"` | Judul kartu. |
| `StableFrames` | `int` | `8` | Jumlah frame live berturut-turut yang harus ditahan sebelum isyarat ditambahkan. |
| `OnSign` | `EventCallback<string>` | | Dipicu saat huruf atau kata ditambahkan. |

```razor
<SignLanguageAssistant StableFrames="10" OnSign="letter => log.Add(letter)" Language="Bahasa Indonesia" />
```

### Pose tubuh

![Yoga trainer](../screenshots/vision-yoga.jpg)

### PoseTracker

Pose tubuh 33 titik dengan sudut sendi, visibilitas, dan mask orang opsional.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Pose tracker"` | Judul kartu. |
| `OnPose` | `Action<PoseLandmarks>?` | `null` | Dipanggil dengan setiap pose yang terdeteksi, di thread pemrosesan. |

```razor
<PoseTracker OnPose="pose => last = pose" Height="480px" />
```

### SkeletonViewer

Rendering kerangka real-time dalam tiga gaya untuk hingga tiga orang, dengan jejak gerak opsional pada pergelangan tangan dan kaki.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Skeleton viewer"` | Judul kartu. |
| `Style` | `string` | `"Neon"` | `Neon`, `Bones`, atau `Minimal`. |
| `Trails` | `bool` | `true` | Menggambar jejak gerak. |
| `TrailLength` | `int` | `25` | Jumlah frame yang disimpan per jejak. |
| `People` | `int` | `1` | Jumlah orang yang dilacak (1–3). |

```razor
<SkeletonViewer Style="Neon" People="2" Trails="true" TrailLength="24" StartWithCamera="true" />
```

### ExerciseMonitor

Analitik kebugaran: menghitung repetisi dengan hysteresis untuk squat, push-up, jumping jack, curl, dan lunge, ditambah petunjuk form dan pelatih AI.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Exercise monitor"` | Judul kartu. |
| `ExerciseName` | `string` | `"Squat"` | `Squat`, `Push-up`, `Jumping jack`, `Bicep curl`, atau `Lunge`. |
| `OnRep` | `EventCallback<int>` | | Dipicu saat satu repetisi selesai, dengan total repetisi. |

```razor
<ExerciseMonitor ExerciseName="Squat" OnRep="reps => total = reps" />
```

### YogaTrainer

Koreksi pose: membandingkan sudut sendi dengan pustaka asana (sisi kiri atau kanan), memberi skor setiap sendi, dan mengukur lama menahan pose.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Yoga trainer"` | Judul kartu. |
| `Asana` | `string` | `"Warrior II"` | Pose target dari pustaka bawaan. |
| `HoldThreshold` | `double` | `80` | Skor (0–100) saat timer menahan pose mulai berjalan. |

```razor
<YogaTrainer Asana="Warrior II" HoldThreshold="80" />
```

### SportsAnalyzer

Analitik gerak: deret waktu sudut sendi, kecepatan anggota tubuh, simetri kiri/kanan, dan deteksi puncak, dengan analis AI.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Sports analyzer"` | Judul kartu. |
| `Pair` | `string` | `"Knee"` | Pasangan sendi yang digrafikkan: `Elbow`, `Shoulder`, `Hip`, atau `Knee`. |
| `WindowSeconds` | `int` | `15` | Panjang jendela deret waktu dalam detik. |
| `Activity` | `string` | `"running drill"` | Olahraga atau latihan yang dianalisis, diberikan ke analis AI. |

```razor
<SportsAnalyzer Activity="Tennis serve" Pair="Elbow" WindowSeconds="15" />
```

### ErgonomicMonitor

Postur kerja: fleksi leher, kemiringan badan, jangkauan lengan, dan bahu terangkat, skor risiko ala RULA, waktu dalam postur berisiko, dan pengingat istirahat.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Ergonomic monitor"` | Judul kartu. |
| `BreakAfterMinutes` | `double` | `20` | Menit dalam postur berisiko sebelum pengingat istirahat muncul. |
| `OnBreakReminder` | `EventCallback<ErgonomicAssessment>` | | Dipicu saat pengingat istirahat ditampilkan. |

```razor
<ErgonomicMonitor BreakAfterMinutes="25" OnBreakReminder="Notify" />
```

### Holistik

### HolisticTracker

Wajah, kedua tangan, dan tubuh dalam satu kali proses (543 landmark), dengan toggle layer dan mask orang opsional.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Holistic tracker"` | Judul kartu. |
| `ShowPose` | `bool` | `true` | Menggambar layer tubuh. |
| `ShowFace` | `bool` | `true` | Menggambar layer face mesh. |
| `ShowHands` | `bool` | `true` | Menggambar layer tangan. |
| `ShowMask` | `bool` | `false` | Menggambar mask orang. |
| `OnResult` | `Action<HolisticResult>?` | `null` | Dipanggil dengan setiap hasil, di thread pemrosesan. |

```razor
<HolisticTracker ShowPose="true" ShowFace="true" ShowHands="true" ShowMask="false" />
```

### HumanDigitalTwin

Digital twin gerakan seseorang: tubuh kapsul 3D yang digerakkan oleh world landmark metrik, dengan tinggi badan, rentang lengan, keseimbangan, dan postur.

Base: `VisionComponentBase`. Membutuhkan stylesheet Blazor.AI.ThreeD.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Human digital twin"` | Judul kartu. |
| `BodyColor` | `string` | `"#d9d4c4"` | Warna tubuh. |
| `AccentColor` | `string` | `ThreeDPalette.Nila` | Warna aksen. |
| `OnMetrics` | `Action<TwinMetrics>?` | `null` | Dipanggil dengan setiap pembaruan metrik, di thread pemrosesan. |

```razor
<HumanDigitalTwin BodyColor="#d9d4c4" AccentColor="#3cc4b1" OnMetrics="m => height = m.Height" />
```

### MotionCaptureStudio

Motion capture tanpa marker: rekam world landmark pose, haluskan, scrub dan putar ulang di atas gambar maupun dalam 3D, lalu ekspor ke JSON atau BVH.

Base: `VisionComponentBase`. Membutuhkan stylesheet Blazor.AI.ThreeD.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Motion capture studio"` | Judul kartu. |
| `Smooth` | `bool` | `true` | Menerapkan smoothing One-Euro pada rekaman live. |
| `MaxSeconds` | `int` | `60` | Durasi take terpanjang dalam detik. |
| `OnTake` | `EventCallback<IReadOnlyList<MocapFrame>>` | | Dipicu saat take selesai, dengan frame-frame yang direkam. |

```razor
<MotionCaptureStudio MaxSeconds="60" Smooth="true" OnTake="SaveTake" />
```

### AvatarController

Menggerakkan avatar 3D dari kamera: tubuh dari world landmark pose, tolehan dan anggukan kepala dari face mesh.

Base: `VisionComponentBase`. Membutuhkan stylesheet Blazor.AI.ThreeD.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Avatar controller"` | Judul kartu. |
| `Mirror` | `bool` | `true` | Membalik kiri dan kanan seperti cermin. |
| `Smooth` | `bool` | `true` | Menerapkan smoothing One-Euro pada landmark live. |
| `HeadGain` | `double` | `1.2` | Pengali untuk tolehan dan anggukan kepala. |
| `BodyColor` | `string` | `"#d9d4c4"` | Warna tubuh. |
| `AccentColor` | `string` | `ThreeDPalette.Nila` | Warna aksen. |

```razor
<AvatarController Mirror="true" HeadGain="1.2" BodyColor="#3d4fb8" AccentColor="#d9964f" />
```

### Segmentasi

![Background removal](../screenshots/vision-background.jpg)

### BackgroundRemoval

Penghapusan background real-time: potong, blur, atau beri warna background dengan selfie segmenter.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Background removal"` | Judul kartu. |

```razor
<BackgroundRemoval StartWithCamera="true" Fps="15" />
```

### GreenScreenReplacement

Green screen virtual: selfie segmenter mempertahankan orangnya, lalu browser memasang gambar, warna, atau unggahanmu sendiri di belakangnya.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Green screen replacement"` | Judul kartu. |
| `Backgrounds` | `IReadOnlyList<BackgroundChoice>?` | `null` | Gambar background yang ditawarkan. `null` memakai `DefaultBackgrounds`. |
| `BackgroundUrl` | `string?` | `null` | URL background awal. `null` dimulai dengan warna. |
| `MaxUploadBytes` | `long` | `8 * 1024 * 1024` | Ukuran unggahan terbesar yang diterima, dalam byte. |

```razor
<GreenScreenReplacement BackgroundUrl="@ThreeDAssets.WorldConcept" StartWithCamera="true" />
```

### SubjectIsolationViewer

Segmentasi orang per bagian dengan model selfie multiclass: rambut, kulit wajah, kulit tubuh, pakaian, dan aksesori sebagai mask terpisah beserta cakupannya.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Subject isolation"` | Judul kartu. |
| `InitialParts` | `IReadOnlyList<string>?` | `null` | Bagian yang dipilih di awal, berdasarkan label model: `hair`, `face-skin`, `body-skin`, `clothes`, `others`. |
| `OnCoverage` | `Action<IReadOnlyDictionary<string, double>>?` | `null` | Dipanggil dengan cakupan per bagian setelah setiap frame, di thread pemrosesan. |

```razor
<SubjectIsolationViewer InitialParts="@(["hair"])" OnCoverage="c => hair = c.GetValueOrDefault("hair")" />
```

### ObjectSegmentationStudio

Segmentasi scene: mask kelas DeepLab v3 beserta cakupannya, ditambah segmentasi berbasis titik (MagicTouch) untuk objek apa pun pada gambar diam.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Object segmentation studio"` | Judul kartu. |
| `MinCoverage` | `double` | `0.003` | Porsi minimum frame agar sebuah kelas ditampilkan. |
| `OnClasses` | `Action<IReadOnlyList<ClassCoverage>>?` | `null` | Dipanggil dengan kelas yang ditemukan setelah setiap frame, di thread pemrosesan. |

```razor
<ObjectSegmentationStudio MinCoverage="0.005" OnClasses="classes => found = classes" />
```

### Tampilan 3D

![3D vision components](../screenshots/vision-3d.jpg)

Component-component ini di-render dengan Three.js dari Blazor.AI.ThreeD dan membutuhkan stylesheet-nya.

### FaceMesh3D

Face mesh 478 titik sebagai point cloud 3D yang bisa diorbit, diwarnai berdasarkan kedalaman atau ekspresi, dengan panah arah pandang dan blendshape live.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Face mesh 3D"` | Judul kartu. |
| `ColorMode` | `string` | `"Depth"` | `Depth`, `Expression`, atau `Mono`. |
| `PointSize` | `double` | `0.035` | Ukuran titik dalam satuan scene. |
| `TopBlendshapes` | `int` | `6` | Jumlah blendshape yang ditampilkan di panel. |

```razor
<FaceMesh3D ColorMode="Depth" PointSize="0.035" TopBlendshapes="6" />
```

### AvatarMirror3D

Cermin ajaib 3D: avatar kapsulmu berdiri di depan panel kaca dan pantulannya bergerak di belakangnya.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Avatar mirror 3D"` | Judul kartu. |
| `BodyColor` | `string` | `"#d9d4c4"` | Warna tubuh. |
| `AccentColor` | `string` | `ThreeDPalette.Nila` | Warna aksen. |

```razor
<AvatarMirror3D BodyColor="#d9d4c4" AccentColor="#3cc4b1" StartWithCamera="true" />
```

### PoseSkeleton3D

Kerangka 3D metrik dari world landmark pose, satu per orang, dengan label sudut sendi live dan sendi dengan visibilitas rendah ditampilkan redup.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Pose skeleton 3D"` | Judul kartu. |

```razor
<PoseSkeleton3D Height="460px" />
```

### GestureSpace3D

Kedua tangan sebagai kerangka 3D beserta gestur yang dikenali; tunjuk untuk menggambar di udara dan kepalkan tangan untuk menghapus.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Gesture space 3D"` | Judul kartu. |
| `Mirror` | `bool` | `true` | Membalik kiri dan kanan seperti cermin (wajar untuk kamera depan). |
| `MaxStrokes` | `int` | `12` | Jumlah maksimum goresan gambar udara yang disimpan. |
| `OnGesture` | `EventCallback<string>` | | Dipicu saat gestur sebuah tangan berubah, dengan nama bawaan seperti `Pointing_Up`. |

```razor
<GestureSpace3D Mirror="true" MaxStrokes="12" OnGesture="g => last = g" StartWithCamera="true" />
```

### MotionReplay3D

Rekam sebuah gerakan, atau muat take mocap JSON, lalu putar ulang dalam 3D dengan onion-skin ghost, jejak sendi, dan kecepatan yang bisa diatur.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Motion replay 3D"` | Judul kartu. |
| `MaxSeconds` | `double` | `30` | Durasi rekaman terpanjang dalam detik. |
| `OnTake` | `EventCallback<IReadOnlyList<MocapFrame>>` | | Dipicu saat take direkam atau dimuat. |

```razor
<MotionReplay3D MaxSeconds="30" OnTake="take => frames = take.Count" />
```

### HumanBodyTracker3D

Menempatkan orang yang dilacak di dalam ruangan: jarak dari kamera (perkiraan pinhole), jalur di lantai, jarak tempuh, dan kecepatan.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Human body tracker 3D"` | Judul kartu. |
| `OnFix` | `Action<BodyFix>?` | `null` | Dipanggil dengan setiap perkiraan posisi, di thread pemrosesan. |

```razor
<HumanBodyTracker3D OnFix="fix => distance = fix.Distance" />
```

### DigitalTwinAvatar

Avatar digital twin lengkap dari holistic landmarker: tubuh, tangan berartikulasi, pose kepala, dan ekspresi, dengan log state dan narator AI.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Digital twin avatar"` | Judul kartu. |
| `BodyColor` | `string` | `"#d9d4c4"` | Warna tubuh. |
| `Mirror` | `bool` | `false` | Membalik kiri dan kanan seperti cermin. |
| `OnStateChanged` | `EventCallback<TwinState>` | | Dipicu saat postur, ekspresi, atau status tangan berubah. |

```razor
<DigitalTwinAvatar Language="Bahasa Indonesia" OnStateChanged="s => log.Add(s.Posture)" />
```

### Deteksi

![Object detection](../screenshots/vision-detection.jpg)

### ObjectDetectionPanel

Deteksi objek umum (EfficientDet-Lite0, 80 kelas COCO) dengan filter kelas, hitungan per kelas, dan id track yang stabil.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Object detection"` | Judul kartu. |
| `Classes` | `IReadOnlySet<string>?` | `null` | Hanya melaporkan kelas COCO ini, misalnya `person`, `car`, `dog`. `null` melaporkan semua 80 kelas. |
| `OnObjects` | `Action<IReadOnlyList<TrackedObject>>?` | `null` | Dipanggil dengan objek yang dilacak di setiap frame, di thread pemrosesan. |

```razor
<ObjectDetectionPanel Classes="@(new HashSet<string> { "person", "car", "dog" })" OnObjects="objects => latest = objects" />
```

### SmartRetailVision

Analitik rak: baris rak, celah kosong, tingkat keterisian terhadap target planogram, dan produk yang terdeteksi, dengan saran restock dari AI.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Smart retail vision"` | Judul kartu. |
| `Target` | `double` | `0.85` | Keterisian rak di bawah rasio ini akan ditandai. |
| `OnLowStock` | `EventCallback<(int Row, double Fill)>` | | Dipicu saat sebuah baris rak turun di bawah `Target`, dengan nomor baris (mulai dari 1) dan keterisiannya. |

```razor
<SmartRetailVision Target="0.9" OnLowStock="e => Notify(e.Row, e.Fill)" />
```

### WarehouseVision

Pemantauan gudang: orang dan kendaraan per zona, jarak aman orang–kendaraan, dan perkiraan keterisian stok di rak.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Warehouse vision"` | Judul kartu. |
| `Zones` | `IReadOnlyList<string>` | `["Aisle A", "Aisle B", "Aisle C"]` | Nama zona vertikal (lorong), dari kiri ke kanan. |
| `OnProximityAlert` | `EventCallback<int>` | | Dipicu saat seseorang berada lebih dekat ke kendaraan daripada jarak aman. |

```razor
<WarehouseVision Zones="@(["Inbound", "Racking", "Dispatch"])" OnProximityAlert="pairs => nearMisses += pairs" />
```

### SafetyHelmetDetector

Kepatuhan APD: orang dideteksi, lalu area kepala (dan opsional badan) setiap orang diperiksa untuk helm proyek dan rompi hi-vis, dengan persentase kepatuhan dan log pelanggaran.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Safety helmet detector"` | Judul kartu. |
| `OnViolation` | `EventCallback<int>` | | Dipicu saat jumlah orang tanpa APD bertambah. |

```razor
<SafetyHelmetDetector OnViolation="count => siren.Trigger(count)" Language="Bahasa Indonesia" />
```

### VehicleDetector

Analitik transportasi: hitungan kendaraan per kelas, garis hitung virtual dengan arah dari tracking, dan hitungan dari waktu ke waktu.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Vehicle detector"` | Judul kartu. |
| `OnCrossing` | `EventCallback<(string Class, string Direction)>` | | Dipicu saat kendaraan melewati garis hitung, dengan kelas dan arahnya (`"in"` atau `"out"`). |

```razor
<VehicleDetector OnCrossing="e => log.Add($"{e.Class} {e.Direction}")" Fps="15" />
```

### Klasifikasi

### ImageClassifierPanel

Klasifikasi gambar umum (EfficientNet-Lite0, 1.000 kelas ImageNet) dengan bar top-k dan region of interest opsional.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Image classifier"` | Judul kartu. |
| `Allowlist` | `IReadOnlySet<string>?` | `null` | Membatasi hasil ke label ImageNet ini. |
| `OnClassified` | `Action<IReadOnlyList<(string Label, float Score)>>?` | `null` | Dipanggil dengan setiap hasil, di thread pemrosesan. |

```razor
<ImageClassifierPanel OnClassified="top => label = top.FirstOrDefault().Label" />
```

### FoodRecognition

Item makanan dari deteksi objek dan kelas makanan ImageNet, lalu perkiraan gizi dari AI yang diberi label jelas sebagai perkiraan.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Food recognition"` | Judul kartu. |

```razor
<FoodRecognition Samples="@([VisionSamples.Food, VisionSamples.Fruit])" Language="Bahasa Indonesia" />
```

### PlantDiseaseDetector

Skrining penyakit tanaman: analisis warna daun (hijau sehat, menguning, lesi cokelat), pencocokan few-shot terhadap contoh yang kamu beri label, dan pendapat opsional dari model vision.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Plant disease detector"` | Judul kartu. |
| `Crop` | `string` | `"tomato"` | Nama tanaman yang dipakai di prompt AI, misalnya `tomato`, `chilli`, atau `rice`. |

```razor
<PlantDiseaseDetector Crop="cabai (chilli)" Language="Bahasa Indonesia" />
```

### FishSpeciesRecognizer

Spesies ikan: kelas ikan ImageNet dipindai pada grid 3×3, ditambah galeri referensi yang bisa kamu perluas dengan spesies berlabel milikmu sendiri.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Fish species recognizer"` | Judul kartu. |

```razor
<FishSpeciesRecognizer Samples="@([VisionSamples.Fish])" />
```

### MedicalImageAssistant

Pembantu alur kerja citra klinis: lihat dengan kontras dan zoom, ukur, beri anotasi, lalu minta observasi terstruktur dari model vision. Bukan diagnosis; gambar hanya disimpan di memori.

Base: `AiComponentBase`. Bekerja dengan gambar unggahan; tanpa kamera.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Medical image assistant"` | Judul kartu. |
| `Height` | `string` | `"520px"` | Tinggi CSS maksimum viewer. |
| `BaseWidth` | `double` | `560` | Lebar tampilan pada zoom 1, dalam piksel CSS. Viewer bisa di-scroll saat di-zoom. |
| `MaxBytes` | `long` | `12 * 1024 * 1024` | Ukuran unggahan terbesar yang diterima, dalam byte. |
| `Modalities` | `IReadOnlyList<string>` | `["Dermoscopy", "Clinical skin photo", "Chest X-ray", "Other X-ray", "Wound photo", "Ophthalmic photo", "Other"]` | Jenis citra yang ditawarkan. |
| `Language` | `string?` | `null` | Bahasa jawaban. `null` mengikuti default model. |

```razor
<MedicalImageAssistant Provider="openai" Modalities="@(["Dermoscopy", "Wound photo"])" />
```

### Copilot

![Vision copilot](../screenshots/vision-copilot.jpg)

### VisionCopilot

Mengobrol dengan kamera: setiap jawaban berpijak pada deteksi lokal (objek, wajah, postur, kelas scene) ditambah frame itu sendiri.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Vision copilot"` | Judul kartu. |
| `Role` | `string` | `"a helpful visual assistant looking through the user's camera"` | Peran yang diberikan ke model. |
| `Suggestions` | `IReadOnlyList<string>` | empat pertanyaan pembuka | Saran pertanyaan yang tampil sebagai chip. |
| `FullAnalysisSeconds` | `double` | `1` | Jeda dalam detik antar-analisis lengkap (wajah, pose, kelas) pada video live; deteksi objek berjalan di setiap frame. |

```razor
<VisionCopilot StartWithCamera="true" Suggestions="@(["What objects are visible?", "How many people are present?"])" />
```

### SceneExplainer

Sekali klik menjelaskan frame saat ini sebagai caption, deskripsi detail, alt text, atau pemeriksaan keselamatan, beserta item terdeteksi yang menjadi dasarnya.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Scene explainer"` | Judul kartu. |

```razor
<SceneExplainer Samples="VisionSamples.Scenes" />
```

### VisualQuestionAnswering

Ajukan pertanyaan bebas tentang gambar unggahan atau contoh, dan simpan riwayat tanya jawabnya.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Visual question answering"` | Judul kartu. |
| `OnAnswer` | `EventCallback<(string Question, string Answer)>` | | Dipicu setelah setiap jawaban. |

```razor
<VisualQuestionAnswering OnAnswer="qa => history.Add(qa)" />
```

### VideoSummarizer

Mengambil sampel dari video, stream, atau kamera setiap k detik, menyimpan timeline deteksi dan kelas, lalu merangkumnya.

Base: `VisionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Video summarizer"` | Judul kartu. |
| `OnTimeline` | `EventCallback<IReadOnlyList<TimelineEntry>>` | | Dipicu dengan timeline yang sudah selesai saat pengambilan sampel berhenti. |
| `AutoSummarise` | `bool` | `true` | Merangkum otomatis saat pengambilan sampel berhenti. |

```razor
<VideoSummarizer AutoSummarise="true" OnTimeline="t => timeline = t" />
```

### Kesehatan

![Fall detection](../screenshots/vision-fall.jpg)

### ElderlyFallDetection

Deteksi jatuh dari pose: penurunan cepat titik tengah pinggul dan badan yang horizontal menandai kemungkinan jatuh, yang dikonfirmasi saat orang tetap di bawah, dengan alert beserta snapshot.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Elderly fall detection"` | Judul kartu. |
| `ConfirmSeconds` | `double` | `4` | Detik seseorang harus tetap di bawah sebelum jatuh dikonfirmasi. |
| `OnStateChanged` | `EventCallback<FallState>` | | Dipicu saat state berubah: `Normal`, `Possible`, atau `Confirmed`. |

```razor
<ElderlyFallDetection StartWithCamera="true" ConfirmSeconds="5" OnStateChanged="OnFall" OnEvent="Notify" />
```

### PatientMovementMonitor

Pemantauan tempat tidur atau kursi: alert keluar dari zona aman, tingkat gerakan, dan pengingat reposisi saat pasien terlalu lama diam.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Patient movement monitor"` | Judul kartu. |
| `PatientName` | `string` | `"Bed 4"` | Label pasien atau tempat tidur yang dipakai di event. |
| `SafeZone` | `(float X, float Y, float W, float H)` | `(0.08f, 0.35f, 0.84f, 0.6f)` | Area tempat tidur atau kursi (ternormalisasi). Keluar dari area ini memicu alert keluar dari tempat tidur. |
| `RepositionMinutes` | `double` | `2` | Menit tanpa gerakan berarti sebelum pengingat reposisi. |

```razor
<PatientMovementMonitor PatientName="Bed 4" SafeZone="(0.2f, 0.25f, 0.6f, 0.7f)" RepositionMinutes="120" StartWithCamera="true" />
```

### RehabilitationTracker

Rehabilitasi rentang gerak: menghitung repetisi, mengukur sudut puncak setiap repetisi terhadap target, dan menulis catatan sesi dengan AI.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Rehabilitation tracker"` | Judul kartu. |

```razor
<RehabilitationTracker StartWithCamera="true" OnEvent="e => session.Add(e)" Language="Bahasa Indonesia" />
```

### MedicalSkillsTrainer

Latihan keterampilan dari landmark tangan dan pose: langkah cuci tangan WHO atau posisi tangan CPR, dengan timer tahan, checklist, skor, dan debrief. Alat bantu latihan, bukan sertifikasi.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Medical skills trainer"` | Judul kartu. |

```razor
<MedicalSkillsTrainer StartWithCamera="true" />
```

### Agritech

### CropVision

Analisis lahan dan kanopi dari RGB: indeks vegetasi excess-green, tutupan kanopi, zona stres (menguning atau mencokelat) sebagai heat map, dan catatan agronomi dari AI.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Crop vision"` | Judul kartu. |
| `Crop` | `string` | `"tomato"` | Nama tanaman yang dipakai di prompt AI. |
| `GridColumns` | `int` | `24` | Resolusi heat grid (kolom). |

```razor
<CropVision Crop="Rice" GridColumns="32" OnEvent="LogField" />
```

### FruitQualityInspector

Grading buah: mendeteksi pisang, apel, dan jeruk, lalu menilai rona kematangan dan cacat bintik gelap di dalam setiap kotak (grade A, B, atau C).

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Fruit quality inspector"` | Judul kartu. |
| `DefectLimitC` | `double` | `0.12` | Porsi bintik gelap di atas nilai ini membuat buah masuk grade C; grade B dimulai dari setengahnya. |
| `OnGraded` | `EventCallback<IReadOnlyList<FruitGrade>>` | | Dipicu dengan grade dari setiap frame yang diproses. |

```razor
<FruitQualityInspector DefectLimitC="0.10" OnGraded="Sort" StartWithCamera="true" />
```

### LivestockMonitor

Hitungan ternak per spesies, id dan jejak yang stabil antar-frame, serta aktivitas (bergerak atau istirahat) dari gerakan kotak.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Livestock monitor"` | Judul kartu. |
| `ExpectedCount` | `int?` | `null` | Jumlah ternak yang diharapkan. Jika yang terlihat lebih sedikit, muncul peringatan ternak hilang. |

```razor
<LivestockMonitor ExpectedCount="24" StartWithCamera="true" Fps="4" OnEvent="Alert" />
```

### AquacultureVision

Kamera kolam dan keramba: indeks warna dan kekeruhan air, aktivitas permukaan dari selisih frame, keberadaan ikan per tile, dan respons makan.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Aquaculture vision"` | Judul kartu. |
| `PondName` | `string` | `"Keramba 1"` | Label kolam atau keramba yang dipakai di event. |
| `TileGrid` | `int` | `3` | Jumlah tile per sisi untuk klasifikasi ikan (`TileGrid` × `TileGrid` potongan per frame). |

```razor
<AquacultureVision PondName="Pond B" TileGrid="4" StartWithCamera="true" />
```

### Pendidikan

### InteractiveScienceVision

Aktivitas sains yang digerakkan tangan: busur derajat dari jari, "magnet" yang gayanya mengikuti hukum kuadrat terbalik, dan penghitung objek, dengan penjelasan dari AI.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Interactive science vision"` | Judul kartu. |

```razor
<InteractiveScienceVision StartWithCamera="true" Language="Bahasa Indonesia" />
```

### GestureLearningPlatform

Pelajaran gestur ala game: tunjukkan gestur target sebelum timer habis, kumpulkan streak, dan naik level.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Gesture learning platform"` | Judul kartu. |
| `RoundSeconds` | `double` | `8` | Detik per ronde di level 1. |

```razor
<GestureLearningPlatform RoundSeconds="6" StartWithCamera="true" OnEvent="e => scores.Add(e)" />
```

### SignLanguageLearningStudio

Latihan mengeja dengan jari: kartu huruf target, meter kecocokan live, progres per huruf, dan bentuk referensi yang kamu rekam untuk bahasa isyaratmu sendiri.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Sign language learning studio"` | Judul kartu. |
| `HoldSeconds` | `double` | `1.2` | Detik huruf target harus ditahan dengan kecocokan minimal 75% agar dianggap dikuasai. |

```razor
<SignLanguageLearningStudio HoldSeconds="1.5" StartWithCamera="true" />
```

### Industri

![Factory safety](../screenshots/vision-safety.jpg)

### FactorySafetyMonitor

Keselamatan pabrik dan lokasi kerja: orang di zona terlarang, petunjuk helm proyek per pekerja, dan posisi membungkuk yang tidak aman, dengan log pelanggaran dan snapshot.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Factory safety monitor"` | Judul kartu. |
| `RestrictedZones` | `IReadOnlyList<(float X, float Y, float W, float H)>` | `[(0.0f, 0.55f, 0.22f, 0.45f)]` | Area terlarang (persegi panjang ternormalisasi) tempat orang tidak boleh berdiri. |
| `HelmetThreshold` | `double` | `0.18` | Porsi piksel berwarna keselamatan di area kepala yang dianggap sebagai helm proyek. |
| `BendLimit` | `double` | `45` | Sudut punggung dalam derajat dari vertikal yang dianggap membungkuk tidak aman. |

```razor
<FactorySafetyMonitor RestrictedZones="@zones" HelmetThreshold="0.2" BendLimit="50" Events="@plantEvents" SourceName="Line 2 · Press" />
```

### WorkerTrackingSystem

Tracking banyak orang dengan id stabil dan jejak jalur; jumlah orang dan dwell time per zona.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Worker tracking"` | Judul kartu. |
| `Zones` | `IReadOnlyList<WorkZone>` | `Loading dock`, `Assembly`, `Storage` | Zona sebagai `WorkZone(Name, X, Y, W, H, Color)` dalam koordinat ternormalisasi. |

```razor
<WorkerTrackingSystem Zones="@zones" StartWithCamera="true" Fps="8" />
```

### AssemblyLineAnalytics

Analitik perakitan manual: tangan yang masuk dan keluar zona kerja menandai siklus; waktu siklus dibandingkan takt, alert saat diam, dan grafik tren.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Assembly line analytics"` | Judul kartu. |
| `Station` | `string` | `"A-3"` | Label stasiun yang dipakai di event. |
| `TaktSeconds` | `double` | `12` | Target waktu siklus dalam detik. |
| `ExitSeconds` | `double` | `0.8` | Detik tangan harus berada di luar zona kerja untuk mengakhiri siklus. |
| `IdleAlertSeconds` | `double` | `20` | Detik diam sebelum event peringatan diterbitkan. |
| `WorkZone` | `(float X, float Y, float W, float H)` | `(0.25f, 0.35f, 0.5f, 0.6f)` | Area kerja di meja (ternormalisasi). |

```razor
<AssemblyLineAnalytics Station="A-3" TaktSeconds="12" IdleAlertSeconds="30" WorkZone="(0.25f, 0.35f, 0.5f, 0.6f)" />
```

### SmartSurveillanceDashboard

Dinding kamera: dua sampai empat tile, masing-masing dengan detektornya sendiri, total orang dan kendaraan dari semua kamera, intrusi area terjaga, dan satu daftar event.

Tanpa panggilan AI. Tidak mewarisi base class. Setiap tile adalah `SurveillanceTile`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Tiles` | `IReadOnlyList<CameraTile>` | contoh `Gate`, `Warehouse`, `Site` | Hingga empat kamera sebagai `CameraTile(Name, Url, GuardedArea)`; URL gambar, video, atau stream. Setiap tile juga bisa beralih ke webcam. |
| `Events` | `VisionEventStream?` | `null` | Stream bersama opsional untuk ikut diterbitkan, misalnya untuk `VisionDashboard`. |
| `TileHeight` | `string` | `"220px"` | Tinggi CSS setiap tile. |
| `Fps` | `double` | `6` | Frame per detik yang diminta per tile. |
| `Class` | `string?` | `null` | Kelas CSS tambahan. |

```razor
<SmartSurveillanceDashboard Tiles="@cameras" Events="@siteEvents" Fps="5" />
```

### SurveillanceTile

Satu tile kamera dari dinding pengawasan: menghitung orang dan kendaraan serta menerbitkan event intrusi untuk area terjaga. Menyediakan `Counts` (orang, kendaraan, intrusi).

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Name` | `string` | `"Camera"` | Nama kamera, juga tampil di tile. |
| `InitialImage` | `string?` | `null` | URL contoh atau stream yang ditampilkan pertama kali. |
| `GuardedArea` | `(float X, float Y, float W, float H)?` | `null` | Area terjaga (ternormalisasi). Orang yang kakinya berada di dalamnya memicu event intrusi. |
| `ShowToolbar` | `bool` | `false` | Menampilkan toolbar sumber kamera. |

```razor
<SurveillanceTile Name="Gate" InitialImage="@VisionSamples.Street.Url" GuardedArea="(0.0f, 0.6f, 0.3f, 0.4f)" />
```

### Analitik

### VisionDashboard

Dashboard operasional di atas `VisionEventStream`: tile KPI, event per menit, event per jenis, hitungan terbaru per sumber, dan ringkasan shift dari AI.

Base: `StreamComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `WindowMinutes` | `int` | `15` | Jendela waktu dalam menit untuk tile event terbaru dan grafik per menit. |
| `Language` | `string?` | `null` | Bahasa ringkasan AI. `null` mengikuti default model. |

```razor
<VisionDashboard Events="@events" WindowMinutes="30" Language="Bahasa Indonesia" />
```

### EventTimeline

Event vision secara kronologis dengan thumbnail snapshot, dikelompokkan per menit, dan bisa difilter berdasarkan tingkat keparahan, sumber, dan teks.

Base: `StreamComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Event timeline"` | Judul kartu. |
| `MaxItems` | `int` | `200` | Jumlah maksimum event yang ditampilkan. |
| `ShowSnapshots` | `bool` | `true` | Menampilkan thumbnail snapshot. |
| `AllowClear` | `bool` | `true` | Menampilkan tombol untuk mengosongkan stream. |
| `MaxHeight` | `string` | `"360px"` | Tinggi CSS maksimum daftar. |

```razor
<EventTimeline Events="@events" MaxItems="500" MaxHeight="480px" />
```

### ActivityHeatmap

Di mana aktivitas terjadi: mengakumulasi posisi orang, tangan, atau tubuh ke dalam heat grid yang meluruh, digambar di atas kamera.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Activity heatmap"` | Judul kartu. |
| `Source` | `HeatSource` | `HeatSource.People` | Apa yang diakumulasi: `People`, `Hands`, atau `Body`. |
| `Columns` | `int` | `32` | Jumlah kolom heat grid. |
| `Rows` | `int` | `18` | Jumlah baris heat grid. |
| `Decay` | `double` | `0.995` | Pengali yang diterapkan ke grid di setiap frame (`1` berarti tidak pernah lupa). |

```razor
<ActivityHeatmap Source="ActivityHeatmap.HeatSource.People" Columns="40" Rows="22" Decay="0.998" StartWithCamera="true" />
```

### DetectionStatistics

Hitungan objek per kelas dalam jendela waktu: bar chart frame saat ini, garis tren per jendela, dan tabel ringkasan dengan rata-rata dan puncak.

Base: `SolutionComponentBase`.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Detection statistics"` | Judul kartu. |
| `Classes` | `IReadOnlySet<string>?` | `null` | Kelas COCO yang dihitung. `null` menghitung semua kelas. |
| `WindowSeconds` | `double` | `10` | Panjang satu jendela waktu dalam detik. |
| `MaxWindows` | `int` | `30` | Jumlah jendela yang disimpan. |
| `ScoreThreshold` | `float` | `0.35f` | Skor deteksi minimum. |

```razor
<DetectionStatistics Classes="@(new HashSet<string> { "person", "car", "truck" })" WindowSeconds="60" MaxWindows="60" />
```

### RealtimeMetrics

Kesehatan pipeline secara live dari `VisionTelemetry`: frame per detik, latensi (rata-rata dan p95), error, dan event, ditambah fps dan latensi per kamera bernama.

Tanpa panggilan AI. Tidak mewarisi base class.

| Parameter | Tipe | Default | Fungsi |
|---|---|---|---|
| `Title` | `string` | `"Realtime metrics"` | Judul kartu. |
| `Sources` | `IReadOnlyList<(string Name, Func<VisionStats?> Stats)>?` | `null` | Component bernama yang dirinci, misalnya `("Safety", () => safety?.CameraStats)`. |
| `IntervalMs` | `int` | `1000` | Interval sampling dalam milidetik. |
| `LatencyBudgetMs` | `double` | `120` | Latensi di atas nilai ini akan disorot. |
| `Class` | `string?` | `null` | Kelas CSS tambahan. |

```razor
<RealtimeMetrics Sources="@Sources()" LatencyBudgetMs="100" />
```
