# 第三方组件声明

ClipNest 本体以 MIT 许可证发布，版权所有 © 2026 JDBeWL，全文见仓库根目录的 [LICENSE](LICENSE)。

本文件列出随程序分发的第三方组件，以及运行时按需下载的模型权重。各组件版权归其原作者所有，以下仅说明许可证与分发要求。

## 随程序分发的组件

### MIT

| 组件 | 版本 | 用途 |
| --- | --- | --- |
| Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent | 11.3.23 | 跨平台 UI 框架 |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM 源生成 |
| Microsoft.Extensions.DependencyInjection | 11.0.0 | 依赖注入 |
| System.Drawing.Common | 10.0.0 | GIF 帧解码 |
| Microsoft.Data.Sqlite | 11.0.0 | SQLite 数据访问 |
| SkiaSharp | 2.88.9 | 图像解码、缩放与绘制（其中 Skia 部分为 BSD-3-Clause） |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | ONNX 推理（`onnxruntime.dll`） |
| Microsoft.ML.Tokenizers | 3.0.0 | 文本分词 |
| HarfBuzzSharp | 8.3.1 | 文字排版（`libHarfBuzzSharp.dll`） |
| .NET 运行时（仅“含 .NET”发行版） | 10.0 | 应用运行时 |

### BSD-3-Clause

| 组件 | 版本 | 用途 |
| --- | --- | --- |
| ANGLE（`av_libglesv2.dll`，Avalonia.Angle.Windows.Natives） | 2.1.x | Avalonia 的 GLES 渲染后端 |

### Apache-2.0

| 组件 | 版本 | 用途 |
| --- | --- | --- |
| Serilog | 4.4.0 | 日志 |
| Serilog.Sinks.File | 7.0.0 | 日志文件输出 |
| Dapper | 2.1.89 | 数据访问 |
| MetadataExtractor | 2.9.3 | EXIF 与图片元数据解析 |

### 其他许可证

| 组件 | 许可证 | 说明 |
| --- | --- | --- |
| Microsoft DirectML（`DirectML.dll`） | Microsoft 软件许可条款 | 允许随应用再分发；完整条款见 Microsoft.AI.DirectML NuGet 包内的 `LICENSE.txt` |
| SQLite（`e_sqlite3.dll`） | Public Domain | — |
| Noto Sans SC（界面字体） | SIL Open Font License 1.1 | 全文见随包分发的 `Assets/Fonts/OFL.txt` |
| LibRaw（`libraw.dll`）0.22.2 | LGPL-2.1 或 CDDL-1.0（双许可） | RAW 内嵌预览解码；以独立 DLL 动态加载，未修改源码，许可证全文见随包 `licenses/LibRaw/` |
| Windows Imaging Component（HEIC/HEIF/AVIF 解码） | 系统组件 | 依赖系统已安装的 HEIF/AVIF 扩展，不随本程序分发 |

## 运行时下载、不随程序分发的模型

| 模型 | 许可证 | 说明 |
| --- | --- | --- |
| Chinese-CLIP ViT-B/16 | Apache-2.0 | 语义向量 |
| Recognize Anything++（RAM++） | Apache-2.0 | 通用标签识别 |
| SigLIP2（google/siglip2-base-patch16-224） | Apache-2.0 | 通用标签识别；视觉权重按需下载，标签嵌入随程序分发 |
| CAMIE Tagger、CAMIE Tagger v2 | GPL-3.0 | 权重由用户在本机按需下载，不随安装包或便携包分发 |
| SmilingWolf WD Tagger v3（EVA02-Large、SwinV2） | Apache-2.0 | 二次元标签与角色识别；权重按需下载 |
| CL Tagger（cella110n/cl_tagger v1.02） | Apache-2.0 | 二次元角色、作品与特征标签识别；权重按需下载 |

## 完整许可文本

| 许可证 | 获取方式 |
| --- | --- |
| MIT | 见 [LICENSE](LICENSE) |
| Apache-2.0 | 见下方附录 |
| BSD-3-Clause | <https://opensource.org/license/bsd-3-clause> |
| SIL OFL 1.1 | 随包分发的 `Assets/Fonts/OFL.txt` |
| LGPL-2.1 | 随包分发的 `licenses/LibRaw/LICENSE.LGPL-2.1.txt` |
| CDDL-1.0 | 随包分发的 `licenses/LibRaw/LICENSE.CDDL-1.0.txt` |

## 附录：Apache License 2.0 全文

```text
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```
