@echo off
rem 一键构建 QuickTranslate（调用 build.ps1，支持 -SelfContained -NoPublish 参数）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
