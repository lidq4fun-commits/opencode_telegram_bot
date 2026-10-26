# Desktop 1.0.17 / 桌面版 1.0.17

## Changes / 更新

- V2 only: remove the V1 client, runtime SDK dependency, bundled engine and installation controls. Old desktop settings migrate to V2; external V1 installations and user configuration are not deleted.
- 仅支持 V2：移除 V1 客户端、SDK 依赖、内置引擎及安装入口；旧桌面设置迁移到 V2，不删除外部 V1 安装或用户配置。
- Scan V2 installations automatically. Blue Bundled badge, gray Apply button, green Applied button and red Uninstall button. Chinese/English controls and feedback.
- 自动扫描已有 V2；内置版本为蓝色标签，应用按钮为灰色，正在应用为绿色，卸载为红色，支持中英文切换。
- Maintain a stable `bin/opencode.cmd` entry point, created during installation and redirected on engine changes. Explicit managed selections are preserved. Restart the local service through the selected executable and restore the Bot if it was running.
- 安装时创建稳定命令入口，切换版本时同步指向当前引擎；保留明确选择的托管版本，通过所选引擎重启本地服务，并恢复原先运行的 Bot。
- Update compares the applied version with the official stable V2 npm release, downloads the pinned release in a visible terminal, verifies it and applies it. Failed downloads do not switch engines; failed application restores the previous selection.
- 更新按钮检查当前应用版本，有新版本时在可见终端下载、验证并应用；下载失败不切换，应用失败恢复原选择。
- Remove individual bundled/app-managed installations with confirmation. External uninstall opens Windows installed apps instead of recursively deleting unrelated files.
- 内置及托管版本可单独确认卸载；外部版本打开 Windows 应用管理，不递归删除未知目录。

## Verification / 验证

- Bot build, lint and typecheck passed.
- `npm test -- --maxWorkers=4`: 2365 passed, 7 skipped.
- Desktop compilation and offline production payload checks passed; V1 engine and SDK are absent.
- Engine-selection regression tests cover the dynamic CLI, explicit managed paths, duplicate PATH prevention, V1 filtering, safe external uninstall, colors and Chinese/English labels.
- Official npm metadata smoke test confirms that a newer applied engine is neither downgraded nor changed, and no download is started.

## Notes / 注意

- Open a new terminal after initially adding the app's bin directory to user PATH. Future engine switches update the same entry point.
- 首次安装后请重新打开终端；后续切换会更新同一个命令入口。
- Shell aliases/functions and machine-level PATH entries can override user PATH. These need to be removed/reordered separately.
- Shell 别名、函数或系统级 PATH 可能覆盖用户 PATH，需要单独清理或调整优先级。
- Real update downloads and full desktop installer interaction were not automated against the user's live installation.
- 未对用户正在使用的安装执行真实更新下载或完整交互安装测试。

Artifacts: `desktop/artifacts/OpenCodeTelegram-Setup-1.0.17.exe` and `desktop/artifacts/OpenCodeTelegram-1.0.17-Windows-x64.zip`.

- Setup: 681,505,280 bytes (649.93 MiB), down 60,668,928 bytes (57.86 MiB / 8.17%) from 1.0.16.
- Setup SHA256: `992AA70D7B9862966843B9713CE68635C336636737E0ACC911639906A33A3A78`.
- Release ZIP SHA256: `448E7438EBC2BF3C3AB652741EF7CE8E8EA25436976406812E579E1D6721556E`.
