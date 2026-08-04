# Lain Anim Layer

Windows 全局字符飞入动画覆盖层。任意程序打字，字符从屏幕边缘沿贝塞尔弧线飞向输入光标。

## 打开

```bash
npm start
```

## 关闭

**方式 1（推荐）**：系统托盘（屏幕右下角）找到青色圆点图标 → 右键 → **Quit**

**方式 2**：任务管理器 → 结束 `electron.exe`（全部）

## 功能

| 按键 | 动画 |
|------|------|
| 字母/数字/符号 | 字符沿贝塞尔弧线飞向光标 |
| Enter / Tab / 方向键 | `↵` `⇥` `↑↓←→` 飞入 |
| Delete / Backspace | 圆点从光标处飞离 |
| 鼠标点击 | 青色粒子扩散 |

## 光标定位（caret-probe.exe）

UIA 探针分层定位（每层失败自动降级）：

1. **TextPattern.GetSelection** — 有选区/退化 range 的控件（记事本等）
2. **DocumentRange 文本估算** — Chromium 无选区时的近似
3. **FocusedElement 矩形** — 字符级元素取中心（Chrome），控件级取左中（QQ）
4. **经典 Win32 caret** — GetGUIThreadInfo
5. **鼠标位置** — 全部失败的兜底

重新编译探针：

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$refs = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2"
& $csc /nologo /target:exe /out:caret-probe.exe `
  /r:"$refs\UIAutomationClient.dll" `
  /r:"$refs\UIAutomationTypes.dll" `
  /r:"$refs\WindowsBase.dll" caret-probe.cs
```
