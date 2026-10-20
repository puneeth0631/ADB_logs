$base = Get-Location
Write-Host "Base: $base"
$files = @(
    "OpenCvSharpExtern.dll",
    "OpenCvSharpExtern.*",
    "*OpenCvSharp*.dll",
    "*.dll"
)
foreach ($f in $files) {
    Write-Host "\n--- Searching for: $f ---"
    Get-ChildItem -Recurse -Filter $f -ErrorAction SilentlyContinue | Select-Object FullName, Length, LastWriteTime | Sort-Object FullName | ForEach-Object { Write-Host $_.FullName }
}
Write-Host "\n--- Bin\Debug files ---"
Get-ChildItem -Path .\bin\Debug -Recurse -ErrorAction SilentlyContinue | Select-Object FullName, Length, LastWriteTime | Sort-Object FullName | ForEach-Object { Write-Host $_.FullName }

Write-Host "\n--- Process architectures ---"
[System.Environment]::Is64BitOperatingSystem.ToString() | Write-Host "Is64BitOperatingSystem"
[System.Environment]::Is64BitProcess.ToString() | Write-Host "Is64BitProcess"
Write-Host "\n--- End ---"