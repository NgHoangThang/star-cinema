<#
  Sao luu co so du lieu Star Cinema (PostgreSQL) va anh admin da tai len.
  Chay tay:   powershell -ExecutionPolicy Bypass -File scripts\backup-db.ps1
  Chay tu dong hang ngay: xem huong dan trong scripts\HUONG-DAN-SAO-LUU.md

  Mat khau KHONG nam trong script nay. pg_dump doc mat khau tu file pgpass.conf cua Windows:
    %APPDATA%\postgresql\pgpass.conf   (mot dong: localhost:5432:*:openpg:MAT_KHAU_CUA_BAN)
#>
param(
    [string]$Db      = "cinema",
    [string]$PgUser  = "openpg",
    [string]$PgHost  = "localhost",
    [int]   $PgPort  = 5432,
    [string]$OutDir  = "D:\Cinema\Backups",
    [int]   $KeepDays = 14,
    [string]$Uploads = "D:\Cinema\CinemaApp\wwwroot\uploads"
)

$ErrorActionPreference = "Stop"

# Tim pg_dump (uu tien PATH, sau do thu muc cai dat mac dinh cua PostgreSQL)
$pgdump = (Get-Command pg_dump -ErrorAction SilentlyContinue).Source
if (-not $pgdump) {
    $pgdump = Get-ChildItem "C:\Program Files\PostgreSQL\*\bin\pg_dump.exe" -ErrorAction SilentlyContinue |
              Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $pgdump) { throw "Khong tim thay pg_dump. Hay cai PostgreSQL client tools hoac them thu muc bin vao PATH." }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dump  = Join-Path $OutDir "$Db-$stamp.backup"
$log   = Join-Path $OutDir "backup.log"

function Write-Log([string]$msg) {
    $line = "{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $msg
    Add-Content -Path $log -Value $line
    Write-Host $line
}

try {
    # 1. Du lieu: dinh dang custom (-F c), nen san, khoi phuc duoc bang pg_restore
    & $pgdump -h $PgHost -p $PgPort -U $PgUser -F c -b -f $dump $Db
    if ($LASTEXITCODE -ne 0) { throw "pg_dump that bai (ma loi $LASTEXITCODE). Kiem tra pgpass.conf va ten CSDL." }

    $size = (Get-Item $dump).Length
    if ($size -lt 10KB) { throw "File sao luu qua nho ($size byte), co the bi loi." }
    Write-Log ("Da sao luu CSDL '{0}': {1} ({2:N1} MB)" -f $Db, $dump, ($size / 1MB))

    # 2. Anh poster / combo admin tai len
    if (Test-Path $Uploads) {
        $zip = Join-Path $OutDir "uploads-$stamp.zip"
        Compress-Archive -Path (Join-Path $Uploads "*") -DestinationPath $zip -Force
        Write-Log ("Da nen anh tai len: {0}" -f $zip)
    }

    # 3. Xoa ban sao luu cu hon $KeepDays ngay
    $limit = (Get-Date).AddDays(-$KeepDays)
    Get-ChildItem $OutDir -File | Where-Object { $_.Name -match '^(cinema.*\.backup|uploads-.*\.zip)$' -and $_.LastWriteTime -lt $limit } |
        ForEach-Object { Remove-Item $_.FullName -Force; Write-Log ("Da xoa ban cu: {0}" -f $_.Name) }

    Write-Log "XONG."
}
catch {
    Write-Log ("LOI: " + $_.Exception.Message)
    exit 1
}