# GPTFastAccountSwitch
用于gpt多个账号的快捷切换使用，原汤化原食，gpt自己的成果（笑）进行了一些小的修改

# Codex 账号切换器

双击目录中的 `CodexAccountSwitcher.exe`。这是本地 Windows 图形程序，无需安装，也不需要 Python 或另外安装 .NET SDK；使用 Windows 的 .NET Framework 4.x。

1. 程序自动读取 EXE 同目录的 `account` 文件夹。每个直接子文件夹是一份账号，至少包含 `auth.json`。移动 EXE 后可用“选择目录”重新选择包含 `account` 的 `.codex` 文件夹。
2. 选中要切换的账号，选择重启 ChatGPT 正式版或 Beta 版。优先选择当前正在运行的版本；检测不到安装时可以选择 OpenAI 的 `ChatGPT.exe`。
3. 默认同时切换账号子文件夹中的 `config.toml`。不想更换模型、插件等配置时，取消该选项。为使登录文件生效，程序会将目标根配置的 `cli_auth_credentials_store` 设为 `"file"`。
4. 点击“切换账号并重启”。程序关闭所有检测到的 ChatGPT 窗口及其后台子进程，保存当前账号刷新的 `auth.json`，备份现有文件，再替换登录文件和配置并重启所选版本。切换前请保存正在进行的工作。

## 记录并保存当前用户

在“新账号名称”输入一个新名字，例如 `work-account` 或 `我的账号`，点击“保存当前账号”（也可以按回车）。程序会创建 `account/输入的名称/`，保存当前正在使用的 `auth.json`、已有的 `config.toml`，以及记录账号信息和保存时间的 `profile-info.json`。保存成功后会刷新列表，并标记为当前账号。

保存操作不关闭或重启 ChatGPT，也不更改当前登录。已有同名文件夹不会被覆盖，请改用新名字。名称不能包含路径分隔符、Windows 禁用字符或保留名称。当前登录未保存到任何账号目录时，窗口会显示当前账号信息和“尚未保存”。

## 权限与错误提示修复

新版不再为后台进程申请全部访问权限，改用关闭进程所需的最小权限，并处理进程已退出或正在退出的情况，避免将正常退出误报为权限问题。关闭顺序从后台子进程到主进程；如果无法确认 ChatGPT 完全退出，程序会停止替换登录文件。

如果确实失败，错误窗口会显示失败步骤、对应文件或进程，以及 Windows 错误码；程序会尝试将错误类型、步骤和调用栈记录到 `.codex/AccountSwitcher/operation-errors.jsonl`。日志不记录令牌、文件内容或原始异常消息。实际的权限拒绝需要退出权限较高的 ChatGPT，或以与 ChatGPT 相同的权限运行切换器。

账号结构：

```text
.codex/
  CodexAccountSwitcher.exe
  auth.json
  config.toml
  account/
    tianyuxing/
      auth.json
      config.toml
    yileina/
      auth.json
      config.toml
```

以后新增账号时，在 `account` 下建立一个普通子文件夹，放入该账号登录后保存的 `auth.json` 和可选的 `config.toml`，然后刷新列表。不支持链接、符号链接或目录联接。

## 备份与故障恢复

每次替换前备份到 `.codex/account-switcher-backups/时间戳-随机编号/`。点击“查看备份”打开该目录。替换过程中失败会自动恢复旧文件；若自动恢复失败，错误窗口会指出对应备份目录。

需要手动恢复时：先完全退出 ChatGPT，再把选定备份中的 `auth.json`、`config.toml` 复制回 `.codex` 根目录，覆盖同名文件，然后打开 ChatGPT。`restore-info.json` 记录原文件是否存在；如果原登录文件或配置不存在，恢复时应移除新建的根目录对应文件。备份保留供恢复，不自动删除。

程序仅检查本地文件结构，无法离线确认服务器是否仍接受登录凭据。凭据已过期、已撤销或账号要求验证时，需要重新登录并更新对应账号目录。成功提示表示文件替换完成且检测到所选 ChatGPT 进程已启动，实际账号显示请在 ChatGPT 中确认。独立运行的 Codex CLI 不会被关闭，切换后请自行重开需要使用新账号的 CLI。

所有操作均在本机完成，不上传账号文件，不把令牌写入日志、检查报告或源码。`auth.json`、账号目录和备份都包含登录凭据，请勿分享。官方说明：[登录凭据与文件存储](https://learn.chatgpt.com/docs/auth)。

## 源码、构建与检查

源码在本目录 `AccountSwitcher.cs`，运行 `build.ps1` 可重新生成 EXE。`app.manifest` 设置为普通用户权限运行。

`CodexAccountSwitcher.exe --check <报告绝对路径>`：只读检查本地账号文件、安装信息、Windows 启动接口和后台进程所需权限，不切换登录、不关闭 ChatGPT，也不输出凭据。

`CodexAccountSwitcher.exe --self-test <报告绝对路径>`：使用独立测试目录和虚拟凭据测试扫描、切换、备份、回滚、刷新令牌保存、配置保留、保存当前用户、同名保护、名称校验、错误处理与并发保护。ChatGPT 关闭/重启使用模拟对象，进程退出测试只操作本程序创建的独立测试进程。测试数据保存在报告旁 `test-work` 子目录。测试不会修改真实账号或重启当前 ChatGPT。
