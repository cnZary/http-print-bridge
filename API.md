# HttpPrintBridge 调用方法（API 契约）

给 agent / 程序看的调用文档。该服务是一个 Windows 本地 HTTP 打印桥：接收 PDF，用 pdfium 逐页光栅化后经 GDI **静默打印**（全程不弹对话框）。

- 仅 Windows 可用（winspool/GDI + 服务目录下的 `pdfium.dll`）
- 请求/响应均为 HTTP + JSON（PascalCase 字段名）；PDF 走原始字节或 multipart
- 鉴权 key 是 **URL 路径段**，不是 Header
- 一次只执行一个打印任务，其余排队

---

## 1. 启动服务

```bash
# 正式版（推荐）：无栈追踪元数据，体积小
dist/win-x64/HttpPrintBridge.exe --key=SECRET --urls=http://127.0.0.1:8080

# 排障版：异常栈帧完整（接口行为完全一致）
dist/win-x64/HttpPrintBridge-debug.exe --key=SECRET --urls=http://127.0.0.1:8080
```

- 未安装则自行构建：`powershell -ExecutionPolicy Bypass -File scripts/publish.ps1`（产出到 `dist/win-x64/`，需要 .NET 10 SDK + MSVC 链接器）
- `pdfium.dll` 必须与 exe 同目录，缺失时进程打印 FATAL 后以 **退出码 2** 结束
- 监听地址默认取 `appsettings.json` 的 `Urls`（`http://0.0.0.0:8080`），用 `--urls=` 覆盖
- **调用方务必自己传 `--key=`**。不传时服务生成随机 key 并打印在启动横幅里（stdout）；配合 `--hide-console` 时还会写入 exe 旁的 `key.txt`
- 无 TLS，默认明文 HTTP。key 在 URL 里会进访问日志和 shell 历史，不要外泄完整 URL

### 配置项（每项优先级：命令行 > 环境变量 > appsettings.json 的 `PrintBridge` 节）

| 命令行 | 环境变量 | appsettings 键 | 默认值 | 说明 |
|--------|----------|----------------|--------|------|
| `--key=` | `PRINTBRIDGE_KEY` | `Key` | （空 → 启动时随机生成） | 访问 key |
| `--urls=` | — | `Urls`（顶层） | `http://0.0.0.0:8080` | 监听地址（ASP.NET 标准键） |
| `--DefaultDpi=` | `PRINTBRIDGE_DEFAULT_DPI` | `DefaultDpi` | `300` | 未传 `dpi` 参数时的渲染 DPI |
| `--MaxUploadBytes=` | `PRINTBRIDGE_MAX_UPLOAD_BYTES` | `MaxUploadBytes` | `209715200`（200 MiB） | PDF 上传大小上限 |
| `--PrintTimeoutSeconds=` | `PRINTBRIDGE_PRINT_TIMEOUT` | `PrintTimeoutSeconds` | `120` | 打印排队等待上限（见 §5） |
| `--hide-console` | `PRINTBRIDGE_HIDE_CONSOLE` | `HideConsole` | `false` | 启动后隐藏控制台窗口 |

`--hide-console` 是布尔开关：裸写等价于 `=true`，也接受 `--hide-console=false`。

---

## 2. 鉴权

key 是路径第一段：`/{key}/...`。用 SHA-256 定长比较，key 错误返回 `401`：

```json
{"Ok":false,"Error":"Invalid or missing access key."}
```

---

## 3. 接口

### 3.1 健康/状态 — `GET /{key}`

（`GET /{key}/` 也生效，尾斜杠不敏感。）

`200`：

```json
{
  "Service": "http-print-bridge",
  "Version": "1.0.0",
  "DefaultPrinter": "Microsoft Print to PDF",
  "DefaultDpi": 300,
  "PrinterCount": 6
}
```

`DefaultPrinter` 为 `null` 表示系统未配置默认打印机。**推荐用它做存活探测。**

### 3.2 打印机列表 — `GET /{key}/list`

`200`：

```json
{
  "Default": "Microsoft Print to PDF",
  "Printers": [
    {
      "Name": "Microsoft Print to PDF",
      "Server": null,
      "Port": "PORTPROMPT:",
      "Driver": "Microsoft Print To PDF",
      "IsDefault": true,
      "IsNetwork": false,
      "IsOffline": false,
      "Status": 0
    }
  ]
}
```

- `Printers` 按 `Name` 不区分大小写排序
- `Server` / `Port` / `Driver` 可能为 `null`
- `Status` 是 winspool `PRINTER_STATUS` 位标志（uint），`0` 表示正常
- 枚举失败时 `500` + `ErrorResponse`

### 3.3 打印 — `POST /{key}/print`（等价于 `POST /{key}`）

**查询参数：**

| 参数 | 必填 | 约束 | 缺省行为 |
|------|------|------|----------|
| `printer` | 否 | 必须是 `/{key}/list` 里 `Name` 的精确值（不区分大小写）；URL 需编码空格等字符 | 用系统默认打印机 |
| `output` | 否 | **必须是绝对路径** | 不重定向 |
| `copies` | 否 | 整数 `1`–`999` | `1` |
| `dpi` | 否 | 整数 `36`–`600` | 配置的 `DefaultDpi`（默认 300） |

注意：`copies` / `dpi` 传了**非数字**会静默按缺省值处理（不报错）；只有"能解析成数字但越界"才返回 `400`。

**请求体（二选一）：**

1. `multipart/form-data`，文件字段名 `file`（没有 `file` 字段时取第一个文件字段）
2. 原始 `application/pdf` 字节流（任意 Content-Type 亦可，服务按字节读）

超过 `MaxUploadBytes` 返回 `413`；空 PDF 返回 `400`。

**成功 `200`：**

```json
{
  "Ok": true,
  "Printer": "Microsoft Print to PDF",
  "Pages": 3,
  "Copies": 1,
  "Dpi": 300,
  "Bytes": 24578
}
```

`Printer` 是解析后实际使用的打印机名，`Bytes` 是收到的 PDF 字节数。HTTP `200` 即表示打印任务已完整提交（spooler 已收下），不代表纸已出机。

**错误（全部为 `{"Ok":false,"Error":"..."}`）：**

| HTTP | 触发条件 | 典型 Error 文本 |
|------|----------|-----------------|
| `401` | key 错误/缺失 | `Invalid or missing access key.` |
| `400` | `copies` 越界 | `copies must be between 1 and 999.` |
| `400` | `dpi` 越界 | `dpi must be between 36 and 600.` |
| `400` | multipart 没有文件字段 | `Could not read upload: multipart/form-data contains no file part (expected field name 'file').` |
| `400` | 无 PDF 数据 | `No PDF received. Send multipart field 'file' or a raw application/pdf body.` |
| `400` | PDF 损坏 | `PDF data is corrupted or not a valid PDF.` |
| `400` | PDF 加密 | `PDF is password protected.`（不支持传密码） |
| `400` | 页渲染内存不足 | `Page N at D DPI requires too much memory.` |
| `400` | 上传读取失败 | `Could not read upload: ...`（读 body 过程中的异常都带这个前缀） |
| `404` | 打印机名不存在 | `Printer not found: 'xxx'.` |
| `413` | 上传超限 | `PDF exceeds the N byte upload limit.` |
| `499` | 客户端中途断开 | `Print job was cancelled.` |
| `500` | `output` 不是绝对路径 | `output must be an absolute file path.` |
| `500` | PDF 页数为 0 | `PDF has no pages.` |
| `500` | GDI/驱动调用失败 | `StartDoc failed for printer 'xxx' (win32 error NNN).`、`CreateDC failed ...`、`EndDoc failed ...` 等 |
| `503` | 已有打印任务在跑且等待超时 | `Another print job is still running.` |

- PDF 解析/渲染错误归 `400`；参数越界归 `400`；打印机/驱动/GDI 错误归 `404`/`500`。
- **Error 文本里的单引号在 JSON 中转义为 `\u0027`**（如 `"Printer not found: \u0027xxx\u0027."`），按 JSON 解码后再匹配文本。

---

## 4. 可直接复制的调用示例

```bash
KEY=mysecret
BASE=http://127.0.0.1:8080

# 1) 探活
curl -s "$BASE/$KEY"

# 2) 列打印机（把返回的 Name 原样用于 printer 参数，空格需 URL 编码为 %20）
curl -s "$BASE/$KEY/list"

# 3) 打印 PDF（multipart，字段名 file）
curl -s -X POST "$BASE/$KEY/print?copies=1&dpi=300" \
  -F "file=@test/test3.pdf;type=application/pdf"

# 4) 打印到指定打印机
curl -s -X POST "$BASE/$KEY/print?printer=Microsoft%20Print%20to%20PDF&copies=2" \
  -F "file=@test/test3.pdf;type=application/pdf"

# 5) 原始字节流方式
curl -s -X POST "$BASE/$KEY/print?dpi=150" \
  -H "Content-Type: application/pdf" \
  --data-binary @test/test3.pdf

# 6) 用虚拟打印机出 PDF 文件（不弹保存框；output 必须是绝对路径且目录已存在，反斜杠/冒号要编码）
curl -s -X POST "$BASE/$KEY/print?printer=Microsoft%20Print%20to%20PDF&output=C%3A%5Ctemp%5Cout.pdf" \
  -F "file=@test/test3.pdf;type=application/pdf"
```

> `output` 是否生效取决于打印机驱动/端口，不是所有组合都支持（见 §5）。调用方必须处理可能的 `500`。

无现成 PDF 时可用仓库自带脚本生成（纯标准库，无第三方依赖）：

```bash
python test/make_test_pdf.py test/out.pdf 3   # 用法: python make_test_pdf.py <输出路径> [页数=3]
```

---

## 5. 行为约束（调用前必读）

- **静默打印**：应用自身不显示任何打印对话框。虚拟打印机（如 `Microsoft Print to PDF`）不传 `output` 时可能由驱动弹出"保存打印输出"框，见下方 `output` 说明。
- **串行执行**：同一时刻只跑一个打印任务。后来的请求最多排队 `PrintTimeoutSeconds`（默认 120s），超时返回 `503`。所以不要并发压同一个实例的 print 接口。
- **纸张自动匹配**：按 PDF 第一页尺寸申请自定义纸张（自动判断横竖），驱动拒绝时回退驱动默认纸张，光栅等比缩放居中到可打印区域。
- **`output` 参数**：把 GDI `StartDoc` 的 spooler 输出重定向到该文件（不再送打印机）。相对路径直接 `500`；目标目录必须已存在，否则 `500`（win32 error 3）。**是否生效取决于驱动/端口**：设计上是给虚拟打印机（如 `Microsoft Print to PDF`）静默出文件用的，但实测该组合在部分环境返回 `500`（win32 error 3003，`ERROR_SPL_NO_STARTDOC`）；调用方需把 `500` 视为可能结果，不要假定必然成功。实体打印机传了它则只会生成文件、不会出纸。
- **渲染方式**：逐页按 `dpi` 光栅化，32bpp 白底；光栅尺寸不超过实际打印到纸面的像素数，缩放时用 HALFTONE、1:1 时直接拷贝。单份拷贝峰值内存约一页；多份拷贝优先用驱动 DEVMODE，驱动不接受时由服务循环提交——全书光栅合计 ≤ 256MB 时每页只渲染一次、多份复用，超限则逐份重新光栅化（内存优先）。
- **加密 PDF 不支持**（没有传密码的入口），会直接 `400`。
- **`Ok` 字段**：错误响应里恒为 `false`，成功响应里恒为 `true`；判断成功请看 HTTP 状态码，不要只看 `Ok`。
- **进程退出码**：`0` 正常退出；`2` 缺 `pdfium.dll`。

---

## 6. 正式版与排障版的区别

| 文件 | 用途 |
|------|------|
| `HttpPrintBridge.exe` | 正式版。已裁剪异常栈追踪元数据（体积小）；API 行为与错误消息一致，但崩溃 dump 里没有托管方法名 |
| `HttpPrintBridge-debug.exe` | 排障版。完整栈追踪，用于复现/定位问题 |

两者监听端口不能相同，同时跑请用不同 `--urls` 与 `--key`。
