# Spec: ADB 后端核心 IPC 第一阶段

## 目标

本 spec 锁定 ADBControl 后端核心第一阶段行为。核心作为前端与 ADB 之间的中间层，通过 IPC 接收前端请求，解析当前平台 ADB 资产，执行 ADB 命令，并返回结构化响应。

## 非目标

- 不实现前端 UI；
- 不实现投屏、截图、输入模拟等高级封装 API；
- 不提交真实 ADB 二进制；
- 不把完整 AOSP 源码复制进主仓库；
- 不允许执行任意系统 shell 命令。

## IPC 请求格式

```json
{
  "id": "1",
  "method": "adb.exec",
  "params": {
    "args": ["devices", "-l"]
  }
}
```

## IPC 响应格式

成功：

```json
{
  "id": "1",
  "ok": true,
  "result": {
    "exitCode": 0,
    "stdout": "...",
    "stderr": "..."
  }
}
```

失败：

```json
{
  "id": "1",
  "ok": false,
  "error": {
    "errorCode": "IPC_METHOD_UNKNOWN",
    "message": "Unknown IPC method: ...",
    "module": "ipc.protocol",
    "recoverable": false
  }
}
```

## 第一阶段 method

### `core.getHostTarget`

返回当前核心识别到的平台 target。

### `adb.asset.current`

返回当前平台将使用的 ADB 资产声明。

### `adb.exec`

参数：

```json
{
  "args": ["devices", "-l"]
}
```

核心行为：

1. 解析当前 OS / CPU 架构；
2. 从 `assets/adb/manifest.json` 查找对应 ADB 资产；
3. 校验 args 不能包含 NUL 字节；
4. 只启动 manifest 指向的 ADB 二进制；
5. 使用参数数组传参，不拼接 shell 字符串；
6. 返回 ADB 的 `exitCode`、`stdout`、`stderr`。

## TDD 场景

以下场景已沉淀为测试：

- 场景：Linux `aarch64` 必须映射为 `linux-arm64`；
- 场景：Linux arm64 没有官方 Platform-Tools 产物时，manifest 必须声明为自建 ADB；
- 场景：manifest 缺失当前 target 时，必须返回 `ADB_ASSET_NOT_FOUND`，不能静默降级；
- 场景：非法 JSON 必须返回 `IPC_INVALID_JSON`；
- 场景：未知 method 必须返回 `IPC_METHOD_UNKNOWN`；
- 场景：`adb.exec` 必须保持参数数组，不拼接 shell 字符串；
- 场景：`adb.exec` 的 `args` 类型错误必须返回 `IPC_PARAMS_INVALID`；
- 场景：args 包含 NUL 字节必须返回 `ADB_ARGS_CONTAIN_NUL`。

## 后续阶段

- 增加 Windows Named Pipe transport；
- 增加 Unix Domain Socket transport；
- 增加 ADB 二进制下载/校验/打包流程；
- 增加独立 AOSP ADB 源码镜像仓库与 GitHub Actions 编译产物同步；
- 增加高级能力 API，例如截图、安装 APK、logcat、shell session、文件传输等。
