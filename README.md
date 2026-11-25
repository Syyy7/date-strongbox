# Date Strongbox · 本地加密备忘录

Date Strongbox 是一款面向 Windows x64 的离线备忘录，用于保存账号、密码和私人笔记。首次打开时设置保险库密码，之后用这个密码在本机解锁。日常使用无需注册账号或连接服务器。

## 下载与启动

在 [GitHub Releases](https://github.com/Syyy7/date-strongbox/releases/latest) 下载 Windows x64 发行压缩包，完整解压到本地文件夹，然后运行 `SecureMemo.App.exe`。压缩包自带 .NET 与 Windows App SDK 运行时。首次使用会提示创建保险库，已有保险库的用户会看到解锁页面。

## 使用

- **记录与搜索**　新建账号或笔记，按标题、账号或标签搜索。编辑后会自动保存，也可以按 `Ctrl+S` 立即保存。
- **密码查看**　在建库、解锁、账号编辑、修改密码和导入备份时，按住密码框旁的眼睛可临时查看，松开即隐藏。使用键盘时，可按 Tab 移至眼睛按钮后按住空格。账号和密码均可单独复制。
- **图标与外观**　右键列表中的项目可更换图标、设置颜色或删除。设置页支持浅色、深色和跟随系统，字号与界面大小可以拖动预览。默认字号为 14，默认缩放为 100%。
- **锁定**　按 `Ctrl+L` 手动锁定。Windows 会话锁定或连续五分钟无操作也会触发锁定。
- **备份与迁移**　在设置中导出 `.smbak` 加密备份。换电脑时，先创建本机保险库，再导入备份并输入导出时的密码。导入会合并记录，具有相同 ID 的条目会跳过。

## 从源码运行

开发环境为 Windows x64、.NET 10 SDK 或兼容的更新版本、Windows SDK。使用 Visual Studio 2026 时，请安装 WinUI 应用程序开发支持。第一次还原依赖需要访问 NuGet；项目的包缓存位于根目录下的 `.packages/`。

用 Visual Studio 打开 [SecureMemo.slnx](SecureMemo.slnx)，将 `SecureMemo.App` 设为启动项目。也可以在仓库根目录执行以下 PowerShell 命令。

```powershell
dotnet restore SecureMemo.slnx
dotnet build SecureMemo.slnx -c Release --no-restore
dotnet run --project SecureMemo.App/SecureMemo.App.csproj -c Release --no-build
```

## 数据与密码

| 保存位置 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\SecureMemo\vault.json` | 加密后的记录。标题、账号、密码、标签和正文都在密文内 |
| `HKEY_CURRENT_USER\Software\SecureMemo\Vaults` | 各保险库的公钥及由密码保护的密钥材料 |
| 用户导出的 `.smbak` | 加密记录和配套密钥信封，供跨机导入 |

建库时随机生成 X25519 密钥对。应用用 Argon2id 从密码和随机盐派生密钥，再用 AES-256-GCM 保护私钥和写入认证密钥。保存记录时使用 HPKE（X25519、HKDF-SHA256、AES-256-GCM）加密，并用独立的 HMAC-SHA256 验证写入。

迁移时请使用设置中的**导出备份**。单独复制 `vault.json` 缺少对应的密钥信封。修改保险库密码后，先前导出的备份仍需使用导出时的密码。请妥善保管密码，应用没有在线找回渠道。

## 项目结构

| 路径 | 用途 |
| --- | --- |
| [SecureMemo.App/](SecureMemo.App/) | 当前 WinUI 3 桌面应用、设置页和主题资源 |
| [SecureMemo.Core/](SecureMemo.Core/) | 保险库存储、密码保护、加密和备份 |
| [tests/SecureMemo.Checks/](tests/SecureMemo.Checks/) | 保险库回归检查及旧版界面冒烟检查 |
| [legacy/WinFormsApp1/](legacy/WinFormsApp1/) | 留存的 WinForms 原型，与当前应用共用存储核心 |
| [docs/开发方案.md](docs/开发方案.md) | 原始设计、数据流程与规划 |

在 Windows 桌面会话中运行以下命令可以检查建库、保存、解锁、篡改检测、备份恢复以及旧版界面的基本操作。检查使用临时的测试保险库。

```powershell
dotnet run --project tests/SecureMemo.Checks/SecureMemo.Checks.csproj -c Release
```
