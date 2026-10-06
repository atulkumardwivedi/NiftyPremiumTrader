$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Start-Process powershell -ArgumentList @(
    '-NoExit',
    '-Command',
    "Set-Location '$root\api'; dotnet restore; dotnet run"
)

Start-Process powershell -ArgumentList @(
    '-NoExit',
    '-Command',
    "Set-Location '$root\ui'; npm install; npm start"
)
