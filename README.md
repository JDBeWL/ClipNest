# ClipNest

ClipNest 是一个面向 Windows 的本地图片图库，面向动漫插画与常规照片管理。它把文件夹扫描、瀑布流浏览、标签整理、AI 自动标注和语义搜索放在同一个离线优先的桌面应用里，图片本身不会上传到云端。

目前仅支持 Windows x64。

## 功能

- **本地图库**：导入多个文件夹，增量扫描图片，自动生成缩略图，标记磁盘上已缺失的文件。
- **浏览与排序**：瀑布流图库、详情栏、按拍摄时间/文件名/评分排序。
- **批量整理**：多选后复制、移动或删除图片；删除走 Windows 回收站，可还原。
- **标签体系**：标签按特征、角色、作品分类；支持手动修改角色与作品。
- **AI 自动标注**：CAMIE Tagger 识别动漫角色、作品与画面特征；RAM++ 与 SigLIP2 识别常规照片中的物体与场景。
- **按文件夹选模型**：可以设置全局默认模型，也可以给单个文件夹指定不同模型，例如动漫文件夹用 CAMIE，照片文件夹用 RAM++ 或 SigLIP2。
- **重新分析**：单张图片右键、文件夹右键或设置页都可以重新识别；显式重新分析会按模型结论重建角色与作品，手动添加的其他标签保留。
- **语义搜索**：使用 Chinese-CLIP 把中文查询与画面内容匹配，支持“搜索文件名、标签或画面内容”。
- **模型管理**：模型首次使用时下载，支持六分片并行下载、取消、卸载和完整性校验。
- **本地优先**：SQLite 数据库、缩略图缓存与模型都保存在本机；仅在下载模型时访问 Hugging Face 或镜像站。
- **外观**：深色/浅色/跟随系统，使用与 Windows 一致的窗口控制按钮。

## AI 模型

模型权重不随仓库或安装包分发，需要在设置页中按需下载。

| 模型 | 用途 | 标签规模 | 下载体积 | 许可证 |
| --- | --- | --- | --- | --- |
| CAMIE Tagger v2 | 动漫角色、作品、特征（默认） | 70,527 | 约 789 MB + 7.8 MB | GPL-3.0 |
| CAMIE Tagger v1 | 动漫角色、作品、特征 | 70,527 | 约 856 MB + 4.8 MB | GPL-3.0 |
| RAM++ | 常规照片中的物体与场景 | 4,585 | 约 429 MB + 478 MB + 标签表 | Apache-2.0 |
| SigLIP2 零样本 | 常规照片中的物体与场景 | 4,585 | 约 186 MB，标签嵌入随程序分发 | Apache-2.0 |
| Chinese-CLIP ViT-B/16 | 中文语义搜索与索引 | 512 维向量 | 约 191 MB + 词表 | Apache-2.0 |

说明：

- RAM++ 与 SigLIP2 只输出通用物体与场景标签，不识别角色与作品；需要动漫角色识别时请使用 CAMIE。
- SigLIP2 按余弦相似度选标签，标签更宽泛，单张耗时低于 RAM++。
- 首次加载模型需要较长时间；模型空闲一段时间后会释放推理会话和内存。
- 模型下载完成后会校验字节数：文件不完整时不会被当作已安装。

## 支持的图片格式

| 类型 | 扩展名 |
| --- | --- |
| 常见格式 | `.jpg` `.jpeg` `.png` `.webp` `.gif` `.bmp` `.tif` `.tiff` |
| HEIC / HEIF / AVIF | `.heic` `.heif` `.avif`（依赖 Windows 已安装的 HEIF/AVIF 扩展） |
| RAW | `.cr2` `.cr3` `.nef` `.arw` `.dng` `.raf` `.orf` `.rw2` `.pef` `.srw`（通过 LibRaw 读取内嵌预览） |

## 环境要求

- Windows 10 版本 2004（内部版本 19041）或更高版本，或 Windows 11，x64。
- 源码构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。
- 使用“不含 .NET”发行版时，需要安装 .NET 10 运行时（建议 Desktop Runtime x64）。
- AI 标注与语义搜索需要 **Microsoft Visual C++ 2015-2022 Redistributable（x64）**，因为 ONNX Runtime DirectML 依赖它。
  - 缺少运行库时 ClipNest 仍可启动和浏览图片，会在侧栏提示安装；安装后重启即可启用 AI。
- GPU 可选。应用优先使用 DirectML，初始化失败时回退到 CPU。

下载 VC++ 运行库：<https://aka.ms/vs/17/release/vc_redist.x64.exe>

## 从源码运行

```powershell
dotnet restore ClipNest.sln
dotnet build ClipNest.sln
dotnet run --project src/ClipNest.App/ClipNest.App.csproj
```

运行测试：

```powershell
dotnet test ClipNest.sln
```

Debug 运行时，数据默认写入构建输出目录下的 `data` 文件夹，例如：

```text
src/ClipNest.App/bin/Debug/net10.0-windows/data/
```

## 构建发行版

发行脚本会生成四种产物：

- 含 .NET 运行时的安装包
- 不含 .NET 运行时的安装包
- 含 .NET 运行时的便携版 ZIP
- 不含 .NET 运行时的便携版 ZIP

```powershell
powershell -ExecutionPolicy Bypass -File scripts/release.ps1
```

可选参数：

```powershell
# 指定版本号，默认读取根目录 VERSION 文件
powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 0.1.1

# 只生成便携版
powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -SkipInstaller

# 只生成安装包
powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -SkipPortable

# 手动指定 Inno Setup 的 ISCC.exe
powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -InnoSetup "D:\Program Files\Inno Setup 7\ISCC.exe"
```

生成结果位于：

```text
artifacts/installer/
artifacts/portable/
```

安装包需要 Inno Setup 6 或 7。脚本会自动查找 `Program Files`、`Program Files (x86)`、`%LOCALAPPDATA%` 和卸载注册表中的 `ISCC.exe`；找不到时跳过安装包，便携版仍会正常生成。

体积说明：

- 含 .NET 版是自包含 + 裁剪发布，解压后约 88 MB。
- 不含 .NET 版是框架依赖发布，解压后约 71 MB。
- “不含 .NET”只表示不捆绑 .NET 运行时，并不表示包内没有任何 .NET 程序集。它仍包含应用自身的托管 DLL，以及 DirectML、ONNX Runtime、SkiaSharp 等原生库。
- HEIC/HEIF/AVIF 通过 Windows Imaging Component（COM 接口）解码，因此不再需要随包分发 WinRT 投影 `Microsoft.Windows.SDK.NET.dll`。

## 数据目录

ClipNest 按以下顺序选择数据目录：

1. 环境变量 `CLIPNEST_HOME`
2. 应用所在目录下的 `data`
3. 如果应用目录不可写，退回 `%LOCALAPPDATA%\ClipNest`

典型内容：

```text
data/
  clipnest.db       SQLite 图库数据库
  settings.json     界面与 AI 设置
  cache/            缩略图等缓存
  models/           按需下载的 AI 模型
  logs/             运行日志
```

便携版的数据默认就在程序目录的 `data` 下，删除该目录即可重置图库。

指定自定义数据目录：

```powershell
$env:CLIPNEST_HOME = "D:\ClipNestData"
.\ClipNest.App.exe
```

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `src/ClipNest.Core` | 领域模型、图片格式、标签与搜索契约 |
| `src/ClipNest.Data` | SQLite 数据库、迁移、仓储与搜索 SQL |
| `src/ClipNest.Imaging` | 文件夹扫描、图片解码、元数据、缩略图 |
| `src/ClipNest.Ai` | ONNX Runtime DirectML、CAMIE/RAM++ 标注、CLIP 语义索引、下载队列 |
| `src/ClipNest.App` | Avalonia 桌面界面、MVVM、依赖注入、设置与窗口 |
| `tests/` | Core、Data、Imaging、Ai、App 的单元测试 |

处理流程大致为：

```text
导入文件夹 → 扫描与缩略图 → AI 标注队列 → 语义索引队列
                                      ↘ 标签、角色、作品
                                      ↘ 语义搜索
```

## 开发约定

- C# 注释遵循 [docs/comment-style-guide.md](docs/comment-style-guide.md)。
- 主要技术栈：.NET 10、Avalonia 11、CommunityToolkit.Mvvm、SQLite + Dapper、SkiaSharp、ONNX Runtime DirectML。
- 模型与数据库迁移采用增量方式，新增字段需要同时补充迁移和测试。

## 第三方组件

- [Avalonia](https://avaloniaui.net/) — 跨平台桌面 UI
- [SkiaSharp](https://github.com/mono/SkiaSharp) — 图片解码与缩略图
- [LibRaw](https://www.libraw.org/) — RAW 内嵌预览
- [ONNX Runtime DirectML](https://onnxruntime.ai/) — 本地模型推理
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) / [Dapper](https://github.com/DapperLib/Dapper) — 本地数据访问
- [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) — EXIF 等元数据
- [Noto Sans SC](https://fonts.google.com/noto/specimen/Noto+Sans+SC) — 界面字体（OFL）

模型来源与许可证：

- CAMIE Tagger：<https://huggingface.co/Camais03/camie-tagger> / <https://huggingface.co/Camais03/camie-tagger-v2>（GPL-3.0）
- RAM++ ONNX：<https://huggingface.co/AX1Y2JP/recognize-anything-plus-model-ONNX>（Apache-2.0）
- Chinese-CLIP：<https://huggingface.co/Xenova/chinese-clip-vit-base-patch16>（Apache-2.0）
