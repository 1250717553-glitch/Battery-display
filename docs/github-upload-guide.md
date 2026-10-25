# GitHub 上传标准流程（可复用于其它项目）

> 这份文档记录本机已验证的完整流程，含踩过的坑。新项目照着走即可。

---

## 0. 前置检查（一次性）

```powershell
git --version          # 需要 2.40+，本机 2.45.1
git config --global user.name   # 应为你的 GitHub 用户名
git config --global user.email  # 应为你的 GitHub 邮箱
```

本机已配置：
- `user.name = 1250717553-glitch`
- `user.email = 1250717553@qq.com`

**若未配置**（首次使用）：

```powershell
git config --global user.name "1250717553-glitch"
git config --global user.email "1250717553@qq.com"
git config --global credential.helper manager
```

> **本机没有 `gh` CLI**，所以一律走「网页建仓库 + git push」路线，不需要安装。

---

## 1. 整理项目（上传前必做）

### 1.1 必须新建的两个文件

**`.gitignore`** —— 排除构建产物和本机残留：

```gitignore
# Build output
bin/
obj/
[Bb]in/
[Oo]bj/
*.user
*.suo
.vs/
.vscode/
.idea/

# Test / debug artifacts
_accept/
_probe/
*.log

# Local scratch
预览图/
*.tmp
*.bak
*.bak-*

# OS
Thumbs.db
.DS_Store
desktop.ini
```

**`LICENSE`**（可选）—— MIT 模板：

```
MIT License

Copyright (c) 2026 1250717553-glitch

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### 1.2 上传前扫描敏感信息

```powershell
# 检查源码里是否含本机路径 / 硬件型号 / 用户名
git ls-files | xargs grep -lE "C:\\Users\\|VID_|PID_|你的设备型号" 2>$null
```

**要处理的典型问题：**
- 硬编码的品牌名 / 设备型号（改成通用描述）
- 本机绝对路径（如 `D:\某项目\`）
- 带真实硬件信息的笔记文件（改为英文精简版，或直接忽略）

### 1.3 截图放哪里

统一放 `docs/screenshots/`，用**英文文件名**：

```
docs/screenshots/
  main-window.png
  tray-icon.png
  qr-poster.png
```

> **为什么英文名**：GitHub 的 raw 链接含中文需 URL 编码，且部分 Markdown 渲染器处理不好。

---

## 2. 写 README（中英双语）

### 2.1 文件命名

| 文件 | 用途 |
|---|---|
| `README.md` | **中文主版**（GitHub 默认显示首页） |
| `README.en.md` | 英文版 |

> **不要用** `READMECh.txt` / `READMEEn.md` 这种命名：
> `.txt` 不会被渲染成 Markdown，且 GitHub 只认 `README.md` 作首页。

### 2.2 语言切换链接（两版互加，放标题下方）

中文版：
```markdown
**中文** | [English](README.en.md)
```

英文版：
```markdown
[中文](README.md) | **English**
```

### 2.3 推荐骨架

```markdown
# 项目名

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**中文** | [English](README.en.md)

一句话简介。

<div align="center">
  <img src="docs/screenshots/qr-poster.png" width="400" alt="扫码打开仓库">
</div>

## 功能特性
## 系统要求
## 构建与运行
## 配置文件
## 截图
## 项目结构
## 许可
```

### 2.4 二维码海报（可选，但效果好）

- 尺寸 **2048×1536（4:3）**，手机查看无黑边
- 二维码纠错等级用 **H**
- 静区（白边）至少留 4 个模块宽度
- **数字/装饰图形用 CSS 画**，不要用 emoji（不同系统渲染不一，会出现方块）

---

## 3. 初始化仓库并提交

```powershell
cd D:\你的项目

git init
git branch -M main
git add -A

# 提交前一定先看一眼文件清单
git diff --cached --name-only
```

**确认没有误包含** `bin/`、`obj/`、`*.log`、本机配置。

```powershell
git commit -m "Initial commit: 项目简述

- 要点 1
- 要点 2"
```

> **提交信息建议用英文或中英混合**，GitHub 上更通用。

---

## 4. 在 GitHub 网页建仓库

1. 打开 https://github.com/new
2. Repository name 填项目名
3. **不要勾选** "Add a README file" / ".gitignore" / "license"（本地已有）
4. Create repository

复制页面给出的 HTTPS 地址，形如：
```
https://github.com/用户名/仓库名.git
```

---

## 5. 关联远程并推送

```powershell
git remote add origin https://github.com/用户名/仓库名.git
git push -u origin main
```

**首次推送会弹浏览器**，登录 GitHub 授权一次即可（凭据由 Git Credential Manager 保存）。

> 若提示 `Authentication failed`，先确保：
> `git config --global credential.helper manager`

---

## 6. 常见问题（都是本机真实遇到过的）

### 6.1 `rejected (fetch first)` / 合并冲突

**原因**：网页建仓库时勾选了 "Add a README"，远程已有提交，与本地历史无共同祖先。

**解决**：

```powershell
git pull origin main --allow-unrelated-histories
```

若报 `CONFLICT (add/add): README.md`：

1. 打开冲突文件，会看到：
   ```
   <<<<<<< HEAD
   （你的完整版）
   =======
   （GitHub 自动生成的简单版）
   >>>>>>> 38b165c...
   ```
2. **删掉 `<<<<<<<`、`=======`、`>>>>>>>` 三行，只保留你要的版本**
3. 然后：

```powershell
git add README.md
git -c core.editor="true" commit --no-edit
git push -u origin main
```

> `core.editor="true"` 是为了跳过 Vim 编辑器（很多人卡在这里）。

### 6.2 中文路径导致截图/脚本失败

**现象**：PowerShell 脚本里路径是 `D:\电量显示插件\` 时报「路径中具有非法字符」。

**原因**：PowerShell 5.1 按 ANSI 读取脚本文件，中文被双重编码。

**解决**：
- 脚本**用纯英文路径**（如 `D:\tmp\`）做中间产物
- 或用 UTF-8 with BOM 保存 `.ps1`
- 或直接在 bash（`win_bash`）里操作，它处理 UTF-8 正常

### 6.3 `git push` 报文件被占用（MSB3021/MSB3027）

**原因**：程序还在运行，锁住了 `bin/` 里的 exe，导致 `dotnet build` 失败——这会影响「提交前构建验证」。

**解决**：
```powershell
Get-Process -Name "你的程序名" -ErrorAction SilentlyContinue | Stop-Process -Force
```

### 6.4 图片在 GitHub 上模糊

**原因**：把 32px 小图在页面里放大显示。

**解决**：**按高分辨率原生渲染**（如开启 `g.ScaleTransform(8,8)`），页面里缩小显示，而不是放大位图。

### 6.5 中文文件名让 `git status` 显示八进制

**现象**：`"\344\272\214\347\273\264..."`

**解决**（只是显示问题）：
```powershell
git config --global core.quotepath false
```

---

## 7. 推送后的验证清单

```powershell
# 本地与远程是否一致
git log --oneline -1
git log --oneline origin/main -1

# 远程文件数
git ls-tree -r --name-only origin/main | wc -l

# README 与图片是否可访问（把 URL 换成你的）
curl -s -o NUL -w "%{http_code}" https://raw.githubusercontent.com/用户名/仓库名/main/README.md
curl -s -o NUL -w "%{http_code}" https://raw.githubusercontent.com/用户名/仓库名/main/docs/screenshots/qr-poster.png
```

全部返回 **200** 即为正常。

---

## 8. 一页速查（复制用）

```powershell
# ===== 新项目上传 =====
cd D:\你的项目

# 1) 整理（手动确认 .gitignore / LICENSE / README 已就位）

# 2) 初始化
git init
git branch -M main
git add -A
git diff --cached --name-only      # 检查清单！

# 3) 提交
git commit -m "Initial commit: 项目简述"

# 4) 网页建仓库后关联
git remote add origin https://github.com/用户名/仓库名.git

# 5) 推送（首次会弹浏览器授权）
git push -u origin main
```

**遇到冲突就用第 6.1 节的命令。**
