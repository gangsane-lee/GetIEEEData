@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo [1/2] 필수 라이브러리 상태 확인...
pip install -q requests urllib3 tqdm scholarly

echo.
echo 크롤링을 시작합니다...
echo ==========================================
python crawler.py
echo ==========================================
pause