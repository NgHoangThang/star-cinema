# check.ps1 - kiểm tra nhanh web Star Cinema
# Chạy: powershell -ExecutionPolicy Bypass -File .\check.ps1 [-Build] [-Login] [-Race]
param(
  [switch]$Build,
  [switch]$Login,
  [switch]$Race
)

Set-Location $PSScriptRoot
$base = "http://localhost:5255"
Add-Type -AssemblyName System.Net.Http
$script:pass = 0
$script:fail = 0

# ---------- Hàm dùng chung ----------
function New-Client {
  $h = New-Object System.Net.Http.HttpClientHandler
  $h.AllowAutoRedirect = $false
  $h.UseCookies = $true
  $h.CookieContainer = New-Object System.Net.CookieContainer
  $c = New-Object System.Net.Http.HttpClient($h)
  $c.Timeout = [TimeSpan]::FromSeconds(30)
  return $c
}

function New-Form($pairs) {
  $d = New-Object 'System.Collections.Generic.Dictionary[string,string]'
  foreach ($k in $pairs.Keys) { $d[[string]$k] = [string]$pairs[$k] }
  return ,$d
}

function Send($client, $method, $path, $form) {
  try {
    if ($method -eq "POST") {
      $content = [System.Net.Http.FormUrlEncodedContent]::new($form)
      $r = $client.PostAsync($base + $path, $content).GetAwaiter().GetResult()
    } else {
      $r = $client.GetAsync($base + $path).GetAwaiter().GetResult()
    }
    $loc = ""
    if ($r.Headers.Location) { $loc = $r.Headers.Location.ToString() }
    return @{ Code = [int]$r.StatusCode; Body = $r.Content.ReadAsStringAsync().GetAwaiter().GetResult(); Loc = $loc }
  } catch {
    return @{ Code = 0; Body = ""; Loc = "" }
  }
}

function Report($ok, $name, $detail) {
  if ($ok) { $script:pass++ } else { $script:fail++ }
  $tag = $(if ($ok) { "OK  " } else { "LỖI" })
  "{0} {1,-56} {2}" -f $tag, $name, $detail
}

function Get-Token($html) {
  $m = [regex]::Match($html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
  if ($m.Success) { return $m.Groups[1].Value }
  return ""
}

function Get-FreeSeats($html) {
  $free = @()
  foreach ($m in [regex]::Matches($html, '<button[^>]*class="a-seat[^"]*"[^>]*data-id="(\d+)"[^>]*>')) {
    if ($m.Value -notmatch 'disabled') { $free += $m.Groups[1].Value }
  }
  return ,$free
}

function Count-Selected($html) {
  return ([regex]::Matches($html, '<button[^>]*class="a-seat[^"]*\bselected\b[^"]*"[^>]*>')).Count
}

function Seat-IsVip($html, $id) {
  $m = [regex]::Match($html, '<button[^>]*class="([^"]*)"[^>]*data-id="' + $id + '"')
  return ($m.Success -and $m.Groups[1].Value -match '\bvip\b')
}

# pct = mức giảm lớn nhất (loại vé hoặc giờ/ngày)
function Ticket-Price($basePrice, $pct, $isVip, $vipAdd) {
  $t = [math]::Round($basePrice * (100 - $pct) / 100000, 0, [MidpointRounding]::AwayFromZero) * 1000
  if ($isVip) { $t += $vipAdd }
  return [int]$t
}

function Get-Cfg($html, $name) {
  $tag = [regex]::Match($html, '<div id="tt-config"[^>]*>')
  if (-not $tag.Success) { return -1 }
  $m = [regex]::Match($tag.Value, 'data-' + $name + '="(\d+)"')
  if ($m.Success) { return [int]$m.Groups[1].Value }
  return -1
}

function Login-Client($email, $pw) {
  $c = New-Client
  $lp = Send $c "GET" "/Account/Login" $null
  $form = New-Form @{ Email = $email; Password = $pw; RememberMe = "false"; ReturnUrl = ""; "__RequestVerificationToken" = (Get-Token $lp.Body) }
  $lr = Send $c "POST" "/Account/Login" $form
  $ok = ($lr.Code -eq 302 -and -not $lr.Loc.Contains("/Account/Login"))
  return @{ Client = $c; Ok = $ok; Code = $lr.Code }
}

function Read-Plain($prompt) {
  $sec = Read-Host $prompt -AsSecureString
  return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}

# Gửi yêu cầu giữ ghế và trả về nơi được chuyển tới
function Try-Hold($client, $showId, $seatList, $ack, $token) {
  $f = @{ showtimeId = $showId; seats = $seatList; "__RequestVerificationToken" = $token }
  if ($ack) { $f["ack"] = "1" }
  return Send $client "POST" "/Booking/Hold" (New-Form $f)
}

# ---------- 1. Build (chỉ khi dùng -Build, và phải TẮT web trước) ----------
if ($Build) {
  "== Build =="
  $out = & dotnet build --nologo -v q 2>&1 | Out-String
  $errs = ([regex]::Matches($out, "error [A-Z]+\d+")).Count
  Report ($errs -eq 0) "dotnet build" "$errs lỗi biên dịch"
  if ($errs -gt 0) { ($out -split "`n") | Where-Object { $_ -match "error" } | Select-Object -First 8 }
  ""
}

# ---------- 2. Các trang không cần đăng nhập ----------
"== Trang công khai =="
$anon = New-Client
$home0 = Send $anon "GET" "/" $null
if ($home0.Code -eq 0) { "Web chưa chạy ở $base. Hãy chạy dotnet run trước."; return }

$tomorrow = (Get-Date).AddDays(1).ToString("yyyy-MM-dd")
$nowList = Send $anon "GET" "/Movies?status=NowShowing" $null
$movieIds = @([regex]::Matches($nowList.Body, "/Movies/Details/(\d+)") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
$cinList = Send $anon "GET" "/Cinemas" $null
$cinemaId = "0"
$cm = [regex]::Match($cinList.Body, "/Cinemas/Details/(\d+)")
if ($cm.Success) { $cinemaId = $cm.Groups[1].Value }

# Tìm một phim có suất chiếu NGÀY MAI chưa bắt đầu (ô giờ không bị mờ)
$movieId = "0"
$showId = "0"; $showId2 = "0"
foreach ($id in ($movieIds | Select-Object -First 10)) {
  $d = Send $anon "GET" "/Movies/Details/${id}?date=$tomorrow" $null
  if ($movieId -eq "0") { $movieId = $id }
  $sms = [regex]::Matches($d.Body, 'class="a-slot\s*"\s+href="/Booking/Seats/(\d+)"')
  if ($sms.Count -gt 0) { $movieId = $id; $pick = @($sms | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique | Get-Random -Count ([Math]::Min(2, $sms.Count))); $showId = $pick[0]; if ($pick.Count -gt 1) { $showId2 = $pick[1] }; break }
}
"Phim thử: $movieId | Rạp thử: $cinemaId | Suất chiếu thử (ngày mai): $showId"
""

# ===== KHU VỰC THÊM TRANG MỚI: mỗi dòng là (đường dẫn, mã mong đợi, chuyển tới đâu) =====
$checks = @(
  @("/", 200, ""), @("/Movies", 200, ""), @("/Movies?status=NowShowing", 200, ""),
  @("/Movies?status=ComingSoon", 200, ""), @("/Movies?q=a", 200, ""),
  @("/Movies/Details/$movieId", 200, ""), @("/Movies/Details/99999999", 404, ""),
  @("/Cinemas", 200, ""), @("/Cinemas/Details/$cinemaId", 200, ""),
  @("/Account/Login", 200, ""), @("/Account/Register", 200, ""), @("/Account/ForgotPassword", 200, ""),
  @("/Booking/Seats/$showId", 302, "/Account/Login"),
  @("/Booking/Combos/1", 302, "/Account/Login"),
  @("/Booking/Review/1", 302, "/Account/Login"),
  @("/Profile", 302, "/Account/Login"), @("/Admin", 302, "/Account/Login"), @("/Admin/Orders", 302, "/Account/Login"),
  @("/Account/ChangePassword", 302, "/Account/Login"),
  @("/css/site.css", 200, ""), @("/css/star.css", 200, ""), @("/css/star-pages.css", 200, ""),
  @("/js/star.js", 200, ""), @("/favicon.svg", 200, "")
)
# ===== HẾT KHU VỰC THÊM TRANG MỚI =====

foreach ($c in $checks) {
  $r = Send $anon "GET" $c[0] $null
  $ok = ($r.Code -eq $c[1])
  if ($ok -and $c[2] -ne "" -and -not $r.Loc.Contains($c[2])) { $ok = $false }
  $extra = ""
  if ($r.Loc -ne "") { $extra = "-> " + $r.Loc }
  Report $ok $c[0] ("nhận $($r.Code), mong đợi $($c[1]) $extra")
}
""

# ---------- 3. Nhật ký của web ----------
"== Nhật ký run.log =="
$logPath = Join-Path $PSScriptRoot "run.log"
if (Test-Path $logPath) {
  try {
    $hits = @(Select-String -Path $logPath -Pattern "^fail:" -Context 0,2)
    Report ($hits.Count -eq 0) "Dòng 'fail:' trong run.log" "$($hits.Count) dòng"
    $hits | Select-Object -First 5 | ForEach-Object { $_.Line; $_.Context.PostContext }
  } catch {
    "(không đọc được run.log lúc này, thử lại sau khi tắt web)"
  }
} else {
  "(chưa có run.log: chạy web bằng: dotnet run 2>&1 | Tee-Object -FilePath run.log)"
}
""

# ---------- 4. Đăng nhập thật và đi hết luồng đặt vé (khi dùng -Login) ----------
if ($Login) {
  "== Luồng đặt vé với tài khoản thử =="
  if ($showId -eq "0") { "Không tìm thấy suất chiếu ngày mai chưa bắt đầu ở thành phố hiện tại. Bỏ qua phần này."; return }

  $email = Read-Host "Email tài khoản thử (đã xác nhận email)"
  $pw = Read-Plain "Mật khẩu (không hiển thị)"
  $a = Login-Client $email $pw
  $pw = ""
  Report $a.Ok "Đăng nhập bằng tài khoản thử" "mã $($a.Code)"
  if (-not $a.Ok) { "Đăng nhập thất bại: kiểm tra email, mật khẩu, và tài khoản đã xác nhận email chưa."; return }
  $u = $a.Client

  foreach ($p in @("/Profile", "/Account/ChangePassword")) {
    $r = Send $u "GET" $p $null
    Report ($r.Code -eq 200) $p "nhận $($r.Code), mong đợi 200"
  }

  $sp = Send $u "GET" "/Booking/Seats/$showId" $null
  Report ($sp.Code -eq 200) "/Booking/Seats/$showId" "nhận $($sp.Code), mong đợi 200"
  $token = Get-Token $sp.Body

  # Cấu hình giá và luật có trên trang chọn ghế
  $cfgBase = Get-Cfg $sp.Body "base"
  $cfgVip = Get-Cfg $sp.Body "vip"
  $pctChild = Get-Cfg $sp.Body "pct-child"
  $timePct = 0
  $childOk = Get-Cfg $sp.Body "child-ok"
  $perAdult = Get-Cfg $sp.Body "child-per-adult"
  Report ($cfgBase -gt 0 -and $cfgVip -ge 0 -and $pctChild -ge 0 -and $timePct -ge 0) "Trang chọn ghế có cấu hình giá vé" "giá gốc $cfgBase, VIP +$cfgVip, trẻ em -$pctChild% (giá gốc đã gồm ưu đãi giờ/ngày)"
  Report ($childOk -ge 0 -and $perAdult -gt 0) "Trang chọn ghế có luật vé trẻ em" "phim thử cho vé trẻ em: $childOk, tối đa $perAdult trẻ/người lớn"

  $free = Get-FreeSeats $sp.Body
  Report ($free.Count -ge 5) "Có ít nhất 5 ghế trống để thử" "$($free.Count) ghế trống"
  if ($free.Count -lt 5 -or $sp.Code -ne 200 -or $cfgBase -le 0) { return }

  # ----- Các phép thử chặn dữ liệu sai và lạm dụng: đều phải bị từ chối, quay lại trang chọn ghế -----
  function Rejected($r) { return ($r.Code -eq 302 -and $r.Loc.Contains("/Booking/Seats/")) }

  $t1 = Try-Hold $u $showId "$($free[0]):Bogus" $true $token
  Report (Rejected $t1) "Loại vé không hợp lệ bị từ chối" "nhận $($t1.Code) -> $($t1.Loc)"

  $t2 = Try-Hold $u $showId "$($free[0]):Student" $true $token
  Report (Rejected $t2) "Vé HSSV (đã bỏ) bị từ chối" "nhận $($t2.Code) -> $($t2.Loc)"

  $t3 = Try-Hold $u $showId "$($free[0]):Adult,$($free[1]):Child" $false $token
  Report (Rejected $t3) "Vé trẻ em mà không tick xác nhận bị từ chối" "nhận $($t3.Code) -> $($t3.Loc)"

  if ($childOk -eq 0) {
    $t4 = Try-Hold $u $showId "$($free[0]):Adult,$($free[1]):Child" $true $token
    Report (Rejected $t4) "Vé trẻ em cho phim không phải P/K bị từ chối" "nhận $($t4.Code) -> $($t4.Loc)"
  } else {
    $t5 = Try-Hold $u $showId "$($free[0]):Child" $true $token
    Report (Rejected $t5) "Vé trẻ em không kèm người lớn bị từ chối" "nhận $($t5.Code) -> $($t5.Loc)"
    $many = "$($free[0]):Adult"
    for ($i = 1; $i -le ($perAdult + 1); $i++) { $many += ",$($free[$i]):Child" }
    $t6 = Try-Hold $u $showId $many $true $token
    Report (Rejected $t6) "Quá số trẻ em cho mỗi người lớn bị từ chối" "$($perAdult + 1) trẻ/1 người lớn: nhận $($t6.Code) -> $($t6.Loc)"
  }

  # ----- Luồng bình thường -----
  # Phim cho vé trẻ em: 1 người lớn + 1 trẻ em (có xác nhận). Ngược lại: 2 người lớn.
  $p0 = $free[0]; $p1 = $free[1]
  $pct0 = [Math]::Max(0, $timePct)
  if ($childOk -eq 1) { $type1 = "Child"; $pct1 = [Math]::Max($pctChild, $timePct); $useAck = $true }
  else { $type1 = "Adult"; $pct1 = [Math]::Max(0, $timePct); $useAck = $false }
  $price0 = Ticket-Price $cfgBase $pct0 (Seat-IsVip $sp.Body $p0) $cfgVip
  $price1 = Ticket-Price $cfgBase $pct1 (Seat-IsVip $sp.Body $p1) $cfgVip
  $seatSum = $price0 + $price1

  $hold = Try-Hold $u $showId "${p0}:Adult,${p1}:${type1}" $useAck $token
  $bm = [regex]::Match($hold.Loc, "/Booking/Combos/(\d+)")
  Report ($hold.Code -eq 302 -and $bm.Success) "Giữ 2 ghế (Người lớn và $type1)" "nhận $($hold.Code) -> $($hold.Loc)"
  if (-not $bm.Success) { return }
  $bid = $bm.Groups[1].Value

  # Ghế vừa giữ phải hiện là "của tôi" (đang chọn)
  $sp2 = Send $u "GET" "/Booking/Seats/$showId" $null
  Report ((Count-Selected $sp2.Body) -eq 2) "Trang chọn ghế hiện 2 ghế của tôi" "$(Count-Selected $sp2.Body) ghế được chọn sẵn"

  # Chọn bắp nước
  $cb = Send $u "GET" "/Booking/Combos/$bid" $null
  Report ($cb.Code -eq 200) "/Booking/Combos/$bid" "nhận $($cb.Code), mong đợi 200"
  $pm = [regex]::Match($cb.Body, 'class="a-cb"\s+data-id="(\d+)"\s+data-name="[^"]*"\s+data-price="(\d+)"')
  $expected = $seatSum
  $items = ""
  if ($pm.Success) {
    $prodId = $pm.Groups[1].Value
    $items = "${prodId}:2"
    $expected = $seatSum + 2 * [int]$pm.Groups[2].Value
  }
  $sv = Send $u "POST" "/Booking/SaveCombos/$bid" (New-Form @{ items = $items; "__RequestVerificationToken" = (Get-Token $cb.Body) })
  Report ($sv.Code -eq 302 -and $sv.Loc.Contains("/Booking/Review/$bid")) "Lưu bắp nước" "nhận $($sv.Code) -> $($sv.Loc)"

  # Tổng tiền do server tính phải khớp với tính tay
  $rv = Send $u "GET" "/Booking/Review/$bid" $null
  Report ($rv.Code -eq 200) "/Booking/Review/$bid" "nhận $($rv.Code), mong đợi 200"
  $tm = [regex]::Match($rv.Body, 'class="a-rvtotal">([^<]+)<')
  $shown = 0
  if ($tm.Success) { $digits = ($tm.Groups[1].Value -replace '[^\d]', ''); if ($digits -ne "") { $shown = [int]$digits } }
  Report ($shown -eq $expected) "Tổng tiền server tính = tính tay" "server $shown, tính tay $expected (vé $price0 + $price1, bắp nước phần còn lại)"
  $decoded = [System.Net.WebUtility]::HtmlDecode($rv.Body)
  Report ($decoded.Contains("Người lớn")) "Trang xác nhận ghi đúng loại vé" "có 'Người lớn'"
  if ($timePct -gt 0) { Report ($decoded.Contains("Ưu đãi")) "Trang xác nhận ghi ưu đãi giờ/ngày" "suất này giảm $timePct%" }

  # Không xem được đơn không tồn tại / của người khác
  $nf = Send $u "GET" "/Booking/Review/99999999" $null
  Report ($nf.Code -eq 404) "/Booking/Review/99999999" "nhận $($nf.Code), mong đợi 404"

  # Hủy giữ ghế, ghế phải mở lại
  $cn = Send $u "POST" "/Booking/Cancel/$bid" (New-Form @{ "__RequestVerificationToken" = (Get-Token $rv.Body) })
  Report ($cn.Code -eq 302) "Hủy giữ ghế" "nhận $($cn.Code)"
  $sp3 = Send $u "GET" "/Booking/Seats/$showId" $null
  Report ((Count-Selected $sp3.Body) -eq 0) "Sau khi hủy không còn ghế được giữ" "$(Count-Selected $sp3.Body) ghế còn được giữ"
  ""

  # ---------- 5. Hai người giành một ghế (khi dùng -Race) ----------
  if ($Race) {
    "== Hai người giành cùng một ghế (3 vòng) =="
    $email2 = Read-Host "Email tài khoản thử THỨ HAI"
    $pw2 = Read-Plain "Mật khẩu tài khoản thứ hai (không hiển thị)"
    $b = Login-Client $email2 $pw2
    $pw2 = ""
    Report $b.Ok "Đăng nhập tài khoản thứ hai" "mã $($b.Code)"
    if (-not $b.Ok) { return }
    $u2 = $b.Client

    $pa = Send $u "GET" "/Booking/Seats/$showId" $null
    $seatsToTry = Get-FreeSeats $pa.Body
    if ($seatsToTry.Count -lt 3) { "Không đủ 3 ghế trống để thử."; return }

    for ($i = 0; $i -lt 3; $i++) {
      $seat = $seatsToTry[$i + 2]
      $tk1 = (Get-Token (Send $u "GET" "/Booking/Seats/$showId" $null).Body)
      $tk2 = (Get-Token (Send $u2 "GET" "/Booking/Seats/$showId" $null).Body)
      $c1 = [System.Net.Http.FormUrlEncodedContent]::new((New-Form @{ showtimeId = $showId; seats = "${seat}:Adult"; "__RequestVerificationToken" = $tk1 }))
      $c2 = [System.Net.Http.FormUrlEncodedContent]::new((New-Form @{ showtimeId = $showId; seats = "${seat}:Adult"; "__RequestVerificationToken" = $tk2 }))
      # Bắn hai yêu cầu gần như cùng lúc
      $task1 = $u.PostAsync($base + "/Booking/Hold", $c1)
      $task2 = $u2.PostAsync($base + "/Booking/Hold", $c2)
      $r1 = $task1.GetAwaiter().GetResult()
      $r2 = $task2.GetAwaiter().GetResult()
      $l1 = ""; if ($r1.Headers.Location) { $l1 = $r1.Headers.Location.ToString() }
      $l2 = ""; if ($r2.Headers.Location) { $l2 = $r2.Headers.Location.ToString() }
      $wins = 0
      if ($l1 -match "/Booking/Combos/") { $wins++ }
      if ($l2 -match "/Booking/Combos/") { $wins++ }
      Report ($wins -eq 1) "Vòng $($i + 1): đúng một người giữ được ghế" "người 1: $l1 | người 2: $l2"

      # Dọn: hủy các đơn vừa tạo
      foreach ($pair in @(@($u, $l1), @($u2, $l2))) {
        $mm = [regex]::Match($pair[1], "/Booking/Combos/(\d+)")
        if ($mm.Success) {
          $tt = Get-Token (Send $pair[0] "GET" "/Booking/Seats/$showId" $null).Body
          Send $pair[0] "POST" "/Booking/Cancel/$($mm.Groups[1].Value)" (New-Form @{ "__RequestVerificationToken" = $tt }) | Out-Null
        }
      }
    }
    ""
  }
}

"== Tổng kết =="
"$($script:pass) đạt, $($script:fail) lỗi"
if ($script:fail -eq 0) { "TẤT CẢ ĐẠT" } else { "CÓ $($script:fail) MỤC LỖI" }