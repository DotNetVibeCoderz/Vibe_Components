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
