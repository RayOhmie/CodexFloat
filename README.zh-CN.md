# CodexFloat

[English](README.md) | [简体中文](README.zh-CN.md)

Windows 单文件悬浮窗工具，用来显示 Codex 剩余用量、重置时间和重置卡到期时间。

主程序：

```text
CodexFloat.exe
```

## 当前形态

这个版本改成极简圆形悬浮窗：

- 默认是约 96x96 的圆形监控球。
- 只在圆形里轮流显示 `5h` 和 `Weekly` 的剩余百分比。
- 圆形中间用动态水位表示剩余用量，100% 接近满水位，用量减少后水位下降。
- 圆形外圈是重置倒计时色条，剩余时间越短颜色越强烈，接近重置时会偏红。
- 左键单击圆形任意位置，或在右键菜单选择 `显示详情`，会在原位置展开成完整信息面板。
- 展开面板里的完整信息固定显示；左侧圆球仍会继续轮流显示 `5h` 和 `Weekly` 的剩余百分比、水位和倒计时色条。
- 展开后鼠标离开完整信息窗口会自动恢复为圆形；也可以再次左键单击窗口，或在右键菜单选择 `隐藏详情` 手动恢复。
- 鼠标左键按住可拖动位置，松开后自动记住。
- 每次启动会优先恢复上一次保存的悬浮窗位置。
- 右键菜单支持显示/隐藏详情、刷新、外观、快捷设置、语言、设置、重置位置、帮助、关于、检查更新和退出。

## 外观

右键悬浮窗，选择：

```text
外观
```

当前内置方案：

- `Traffic Classic`
- `Traffic Blue`
- `Minimal Dark`
- `Warm Amber`

选中的外观会保存到：

```text
%APPDATA%\CodexFloat\config.json
```

## 设置项

设置窗口和悬浮窗右键菜单的 `快捷设置` 都支持：

- 开机自动启动。
- 不透明度，范围 35-100。
- 总是置顶。
- 鼠标穿透。
- 锁定窗口位置。

`环境安全监测` 支持：

- 软件启动时自动检测当前 IP 和所在地。
- 检测到中风险环境后，手动刷新时是否再次弹窗确认。
- 检测到高风险环境后，手动刷新时是否强制再次检测。
- 已由用户确认安全的中风险环境会保存到受信任地址库，可从 `环境安全监测` 菜单查看和删除过期记录。
- 当没有任何受信任地址库记录时，CodexFloat 会单独询问是否将当前 IP 和地址设为默认受信任环境。

鼠标穿透开启后，悬浮窗会把鼠标事件传给下层窗口；需要修改设置时，可通过右下角托盘图标的 `Behavior` 菜单或 `Settings` 关闭穿透。

## 语言

悬浮窗右键菜单和右下角托盘图标右键菜单都提供 `语言` / `Language` 菜单。

当前支持：

- 简体中文
- English

切换语言后，托盘菜单、悬浮窗菜单、详情面板、设置窗口和关于窗口会使用同一套语言。

开机自动启动使用当前 Windows 用户的注册表启动项：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

## 帮助和关于

- `帮助` 会打开预留的项目 GitHub 页面。
- `关于` 会显示联系作者和项目 GitHub。
- `检查更新` 会打开项目 Releases 页面。

当前预留地址：

```text
https://github.com/RayOhmie/CodexFloat
https://github.com/RayOhmie
```

## 显示内容

圆形默认只滚动两项：

- `5h` 剩余百分比和重置倒计时。
- `Weekly` 剩余百分比和重置倒计时。

展开后显示：

- 5 小时窗口剩余额度、进度条、重置倒计时。
- 1 周窗口剩余额度、进度条、重置倒计时。
- 两张或多张重置卡的到期倒计时和具体时间。
- 模型 IQ 参考分数，数据来源为 [Codex Radar](https://codexradar.com/)，感谢 Codex Radar 提供数据参考。

环境安全监测使用公共 IP 定位数据。中文界面下，CodexFloat 会额外获取中文所在地显示文本；这部分中文文本只用于显示，不作为环境风险判断依据。
当用户把中风险环境确认为安全时，CodexFloat 会把当前 IP 和地址加入受信任地址库，而不是覆盖掉原来的唯一记录。

重置时间格式：

```text
即将于 2 h 03 m 后重置 (2026-06-22 18:30)
即将于 3 d 04 h 后重置 (2026-06-25 12:00)
```

## 凭据

配置文件位置：

```text
%APPDATA%\CodexFloat\config.json
```

首次启动时，如果新配置不存在，程序会尝试从旧路径迁移一次：

```text
%APPDATA%\CodexResetMonitor\config.json
```

`ACCESS_TOKEN` 和 `ACCOUNT_ID` 都使用 Windows DPAPI 按当前 Windows 用户加密保存。

保存后的字段名：

```text
access_token_dpapi
account_id_dpapi
```

如果旧配置里还有明文 `account_id`，下次保存设置时会自动迁移成 `account_id_dpapi`，不再继续写入明文。

## 自动读取

如果配置缺失，程序启动时会先尝试从 Windows 常见默认路径读取 Codex/ChatGPT 本地 JSON 配置；设置窗口里也有 `自动读取` 按钮。

默认扫描范围包括：

```text
%APPDATA%\Codex
%APPDATA%\OpenAI
%APPDATA%\ChatGPT
%LOCALAPPDATA%\Codex
%LOCALAPPDATA%\OpenAI
%LOCALAPPDATA%\ChatGPT
%LOCALAPPDATA%\Packages\*OpenAI* / *ChatGPT* / *Codex*
%USERPROFILE%\.codex
%USERPROFILE%\.config\codex
```

只有同一个 JSON 文件里同时找到 `ACCESS_TOKEN` 和 `ACCOUNT_ID` 时才会自动导入。导入后会立即用 DPAPI 加密；设置界面不会显示明文。

## 手动输入

设置窗口仍支持手动输入 `ACCESS_TOKEN` 和 `ACCOUNT_ID`，但输入框是密码遮盖模式。

如果已经保存过凭据，设置界面只显示星号遮盖。保留星号或留空表示继续使用现有加密凭据；输入新值并保存才会替换。

## 接口

默认接口：

```text
https://chatgpt.com/backend-api/wham/usage
https://chatgpt.com/backend-api/wham/rate-limit-reset-credits
```

用量映射：

```text
rate_limit.primary_window   -> 5h
rate_limit.secondary_window -> Weekly
```

界面显示的是剩余用量：

```text
remaining = 100 - used_percent
```

这些是 ChatGPT/Codex 的内部 `backend-api` 接口，不是公开稳定的 OpenAI API，后续官方可能会调整字段。

## 文件

- `CodexFloat.exe`: 主程序。
- `CodexFloat.cs`: 当前圆形悬浮窗版本源码。

