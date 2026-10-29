# check-pay.ps1 - kiểm tra thanh toán tiền mặt, chuyển khoản và quyền admin
# Chạy: powershell -ExecutionPolicy Bypass -File .\check-pay.ps1
# Cần 2 tài khoản đã xác nhận email: một ADMIN và một THƯỜNG.
# Bài thử này BÁN THẬT 1 ghế (đơn tiền mặt được duyệt); cuối bài có lệnh SQL xóa đơn thử.
Set-Location $PSScriptRoot
$base = "http://localhost:5255"
Add-Type -AssemblyName System.Net.Http
$script:pass = 0
$script:fail = 0

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
  "{0} {1,-58} {2}" -f $tag, $name, $detail
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

function Seat-HasClass($html, $id, $cls) {
  $m = [regex]::Match($html, '<button[^>]*class="([^"]*)"[^>]*data-id="' + $id + '"')
  return ($m.Success -and $m.Groups[1].Value -match ('\b' + $cls + '\b'))
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

# Giữ 1 ghế rồi chọn cách thanh toán (Cash hoặc Transfer). Trả về mã đơn.
function Start-Order($client, $showId, $seatId, $method) {
  $sp = Send $client "GET" "/Booking/Seats/$showId" $null
  $hold = Send $client "POST" "/Booking/Hold" (New-Form @{ showtimeId = $showId; seats = "${seatId}:Adult"; "__RequestVerificationToken" = (Get-Token $sp.Body) })
  $m = [regex]::Match($hold.Loc, "/Booking/Combos/(\d+)")
  if (-not $m.Success) { return @{ Ok = $false; Id = ""; Why = "giữ ghế: nhận $($hold.Code) -> $($hold.Loc)" } }
  $bid = $m.Groups[1].Value
  $pay = Send $client "GET" "/Payment/Pay/$bid" $null
  $ch = Send $client "POST" "/Payment/Choose/$bid" (New-Form @{ method = $method; agree = "1"; "__RequestVerificationToken" = (Get-Token $pay.Body) })
  $okw = ($ch.Code -eq 302 -and $ch.Loc.Contains("/Payment/Wait/$bid"))
  return @{ Ok = $okw; Id = $bid; Why = "chọn cách thanh toán: nhận $($ch.Code) -> $($ch.Loc)" }
}

$anon = New-Client
$h0 = Send $anon "GET" "/" $null
if ($h0.Code -eq 0) { "Web chưa chạy ở $base. Hãy chạy dotnet run trước."; return }

"== Đăng nhập hai tài khoản =="
$adminEmail = Read-Host "Email tài khoản ADMIN"
$adminPw = Read-Plain "Mật khẩu ADMIN (không hiển thị)"
$a = Login-Client $adminEmail $adminPw
$adminPw = ""
Report $a.Ok "Đăng nhập ADMIN" "mã $($a.Code)"
$userEmail = Read-Host "Email tài khoản THƯỜNG (không phải admin)"
$userPw = Read-Plain "Mật khẩu tài khoản thường (không hiển thị)"
$u = Login-Client $userEmail $userPw
$userPw = ""
Report $u.Ok "Đăng nhập tài khoản THƯỜNG" "mã $($u.Code)"
if (-not ($a.Ok -and $u.Ok)) { "Đăng nhập thất bại: kiểm tra email, mật khẩu, và đã xác nhận email chưa."; return }
if ($adminEmail.Trim().ToLower() -eq $userEmail.Trim().ToLower()) { "Hai tài khoản phải khác nhau."; return }
$adm = $a.Client
$usr = $u.Client
""

"== Quyền truy cập trang quản trị =="
$r = Send $anon "GET" "/Admin" $null
Report ($r.Code -eq 302 -and $r.Loc.Contains("/Account/Login")) "Chưa đăng nhập: /Admin chuyển sang đăng nhập" "nhận $($r.Code)"
$r = Send $anon "GET" "/Admin/Orders" $null
Report ($r.Code -eq 302 -and $r.Loc.Contains("/Account/Login")) "Chưa đăng nhập: /Admin/Orders chuyển sang đăng nhập" "nhận $($r.Code)"
$r = Send $usr "GET" "/Admin" $null
Report ($r.Code -eq 404) "Tài khoản thường: /Admin trả 404" "nhận $($r.Code)"
$r = Send $usr "GET" "/Admin/Orders" $null
Report ($r.Code -eq 404) "Tài khoản thường: /Admin/Orders trả 404" "nhận $($r.Code)"
$r = Send $adm "GET" "/Admin" $null
Report ($r.Code -eq 200) "ADMIN: /Admin vào được" "nhận $($r.Code)"
""

# Chọn ngẫu nhiên một suất chiếu ngày mai (tránh dồn nhiều lần giữ ghế vào cùng một suất)
$tomorrow = (Get-Date).AddDays(1).ToString("yyyy-MM-dd")
$nowList = Send $anon "GET" "/Movies?status=NowShowing" $null
$movieIds = @([regex]::Matches($nowList.Body, "/Movies/Details/(\d+)") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
$slots = @()
foreach ($id in ($movieIds | Select-Object -First 10)) {
  $d = Send $anon "GET" "/Movies/Details/${id}?date=$tomorrow" $null
  foreach ($m in [regex]::Matches($d.Body, 'class="a-slot\s*"\s+href="/Booking/Seats/(\d+)"')) { $slots += $m.Groups[1].Value }
}
$slots = @($slots | Select-Object -Unique)
if ($slots.Count -eq 0) { "Không tìm thấy suất chiếu ngày mai ở thành phố hiện tại. Dừng."; return }
$showId = $slots | Get-Random

$sp = Send $usr "GET" "/Booking/Seats/$showId" $null
$free = Get-FreeSeats $sp.Body
Report ($sp.Code -eq 200 -and $free.Count -ge 3) "Suất thử có ít nhất 3 ghế trống" "suất $showId, $($free.Count) ghế trống"
if ($sp.Code -ne 200 -or $free.Count -lt 3) { return }
$seat1 = $free[0]
$seat2 = $free[1]
$tokenU = Get-Token $sp.Body
""

"== Đơn 1: tiền mặt, admin duyệt =="
$o1 = Start-Order $usr $showId $seat1 "Cash"
Report $o1.Ok "Giữ ghế và chọn Tiền mặt" $o1.Why
if (-not $o1.Ok) { return }
$bid1 = $o1.Id

$w = Send $usr "GET" "/Payment/Wait/$bid1" $null
$cm = [regex]::Match($w.Body, '<b>(SC[A-Z2-9]{8})</b>')
$code1 = $(if ($cm.Success) { $cm.Groups[1].Value } else { "" })
Report ($w.Code -eq 200 -and $code1 -ne "") "Trang chờ hiện mã đặt vé" "mã $code1"

$st = Send $usr "GET" "/Payment/Status/$bid1" $null
Report ($st.Body.Contains("WaitingPayment")) "Trạng thái đơn là WaitingPayment" $st.Body

$r = Send $adm "GET" "/Payment/Wait/$bid1" $null
Report ($r.Code -eq 404) "Người khác không xem được trang chờ của đơn" "nhận $($r.Code)"
$r = Send $adm "GET" "/Payment/Status/$bid1" $null
Report ($r.Code -eq 404) "Người khác không hỏi được trạng thái đơn" "nhận $($r.Code)"
$r = Send $usr "GET" "/Payment/Done/$bid1" $null
Report ($r.Code -eq 302) "Chưa duyệt thì chưa vào được trang đã thanh toán" "nhận $($r.Code)"

$r = Send $usr "POST" "/Admin/Approve/$bid1" (New-Form @{ "__RequestVerificationToken" = $tokenU })
Report ($r.Code -eq 404) "Tài khoản thường không tự duyệt được đơn của mình" "nhận $($r.Code)"

$ao = Send $adm "GET" "/Admin/Orders?status=WaitingPayment" $null
$dec = [System.Net.WebUtility]::HtmlDecode($ao.Body)
Report ($ao.Code -eq 200 -and $code1 -ne "" -and $dec.Contains($code1) -and $dec.Contains("Đã nhận tiền")) "Admin thấy đơn chờ và nút Đã nhận tiền" "nhận $($ao.Code)"
$ap = Send $adm "POST" "/Admin/Approve/$bid1" (New-Form @{ "__RequestVerificationToken" = (Get-Token $ao.Body) })
Report ($ap.Code -eq 302 -and $ap.Loc.Contains("/Admin/Orders")) "Admin bấm Đã nhận tiền" "nhận $($ap.Code) -> $($ap.Loc)"

$st = Send $usr "GET" "/Payment/Status/$bid1" $null
Report ($st.Body.Contains("Paid")) "Khách thấy đơn đã chuyển sang Paid" $st.Body
$dn = Send $usr "GET" "/Payment/Done/$bid1" $null
Report ($dn.Code -eq 200 -and $code1 -ne "" -and $dn.Body.Contains($code1)) "Trang Đặt vé thành công hiện đúng mã" "nhận $($dn.Code)"
$tl = Send $usr "GET" "/Tickets" $null
Report ($tl.Code -eq 200 -and $code1 -ne "" -and $tl.Body.Contains($code1)) "Vé của tôi liệt kê đơn vừa mua" "nhận $($tl.Code)"
$td = Send $usr "GET" "/Tickets/Details/$bid1" $null
Report ($td.Code -eq 200 -and $td.Body.Contains("data:image/png;base64") -and $td.Body.Contains($code1)) "Trang vé có mã QR và mã đặt vé" "nhận $($td.Code)"
$tx = Send $adm "GET" "/Tickets/Details/$bid1" $null
Report ($tx.Code -eq 404) "Người khác không xem được vé của khách" "nhận $($tx.Code)"
$ta = Send $anon "GET" "/Tickets" $null
Report ($ta.Code -eq 302 -and $ta.Loc.Contains("/Account/Login")) "Chưa đăng nhập: /Tickets chuyển sang đăng nhập" "nhận $($ta.Code)"

$ap2 = Send $adm "POST" "/Admin/Approve/$bid1" (New-Form @{ "__RequestVerificationToken" = (Get-Token $ao.Body) })
$st2 = Send $usr "GET" "/Payment/Status/$bid1" $null
Report ($ap2.Code -eq 302 -and $st2.Body.Contains("Paid")) "Bấm duyệt lần hai không gây lỗi, đơn vẫn Paid" "nhận $($ap2.Code)"

$ap3 = Send $adm "GET" "/Admin/Orders?status=Paid&code=$code1" $null
$dec3 = [System.Net.WebUtility]::HtmlDecode($ap3.Body)
Report ($dec3.Contains($adminEmail.Trim())) "Đơn đã duyệt ghi tên người duyệt" "email admin có trong danh sách"

$sp2 = Send $usr "GET" "/Booking/Seats/$showId" $null
Report (Seat-HasClass $sp2.Body $seat1 "sold") "Ghế của đơn đã duyệt hiện là đã bán" "ghế $seat1"
""

"== Đơn 2: chuyển khoản QR, admin hủy =="
$o2 = Start-Order $usr $showId $seat2 "Transfer"
Report $o2.Ok "Giữ ghế và chọn Chuyển khoản" $o2.Why
$code2 = ""
if ($o2.Ok) {
  $bid2 = $o2.Id
  $w2 = Send $usr "GET" "/Payment/Wait/$bid2" $null
  $cm2 = [regex]::Match($w2.Body, '<b>(SC[A-Z2-9]{8})</b>')
  if ($cm2.Success) { $code2 = $cm2.Groups[1].Value }
  Report ($w2.Body.Contains("data:image/png;base64")) "Trang chờ có mã QR" "mã $code2"

  $r = Send $usr "POST" "/Admin/Reject/$bid2" (New-Form @{ "__RequestVerificationToken" = (Get-Token $w2.Body) })
  Report ($r.Code -eq 404) "Tài khoản thường không tự hủy qua trang admin" "nhận $($r.Code)"

  $ao2 = Send $adm "GET" "/Admin/Orders?status=WaitingPayment" $null
  $rj = Send $adm "POST" "/Admin/Reject/$bid2" (New-Form @{ "__RequestVerificationToken" = (Get-Token $ao2.Body) })
  Report ($rj.Code -eq 302) "Admin hủy đơn chờ" "nhận $($rj.Code)"
  $st3 = Send $usr "GET" "/Payment/Status/$bid2" $null
  Report ($st3.Body.Contains("Cancelled")) "Đơn chuyển sang Cancelled" $st3.Body
  $sp3 = Send $usr "GET" "/Booking/Seats/$showId" $null
  Report (-not (Seat-HasClass $sp3.Body $seat2 "held") -and -not (Seat-HasClass $sp3.Body $seat2 "sold")) "Ghế của đơn bị hủy đã được nhả" "ghế $seat2"
}
""
"== Quét vé ở cổng rạp =="
if ($code1) {
  $scAnon = Send $anon "GET" "/Admin/Scan" $null
  Report ($scAnon.Code -eq 302) "Chưa đăng nhập: /Admin/Scan chuyển sang đăng nhập" "nhận $($scAnon.Code)"
  $scUsr = Send $usr "GET" "/Admin/Scan" $null
  Report ($scUsr.Code -eq 404) "Tài khoản thường: /Admin/Scan trả 404" "nhận $($scUsr.Code)"
  $scAdm = Send $adm "GET" "/Admin/Scan" $null
  Report ($scAdm.Code -eq 200) "ADMIN: /Admin/Scan vào được" "nhận $($scAdm.Code)"

  $tkAnon = Send $anon "GET" "/Admin/Ticket/$code1" $null
  Report ($tkAnon.Code -eq 302) "Chưa đăng nhập: trang kiểm tra vé chuyển sang đăng nhập" "nhận $($tkAnon.Code)"
  $tkUsr = Send $usr "GET" "/Admin/Ticket/$code1" $null
  Report ($tkUsr.Code -eq 404) "Tài khoản thường không xem được trang kiểm tra vé" "nhận $($tkUsr.Code)"

  $tkAdm = Send $adm "GET" "/Admin/Ticket/$code1" $null
  $tkTxt = [System.Net.WebUtility]::HtmlDecode($tkAdm.Body)
  Report ($tkAdm.Code -eq 200 -and $tkTxt.Contains("CHƯA ĐẾN GIỜ VÀO RẠP")) "Vé suất ngày mai báo chưa đến giờ vào rạp" "nhận $($tkAdm.Code)"

  $tkBad = Send $adm "GET" "/Admin/Ticket/SCXXXXXXXX" $null
  $tkBadTxt = [System.Net.WebUtility]::HtmlDecode($tkBad.Body)
  Report ($tkBadTxt.Contains("KHÔNG TÌM THẤY VÉ")) "Mã vé không tồn tại báo không tìm thấy" "nhận $($tkBad.Code)"

  $ci = Send $adm "POST" "/Admin/Ticket/$code1/CheckIn" (New-Form @{ "__RequestVerificationToken" = (Get-Token $scAdm.Body) })
  $tkAfter = Send $adm "GET" "/Admin/Ticket/$code1" $null
  $tkAfterTxt = [System.Net.WebUtility]::HtmlDecode($tkAfter.Body)
  Report (-not $tkAfterTxt.Contains("Đã quét")) "Cố cho vào khi chưa đến giờ bị từ chối, vé không bị ghi đã quét" "nhận $($ci.Code)"
}
else { Report $false "Quét vé: không có mã đơn để thử" "bỏ qua" }
""

"== Tổng kết =="
"$($script:pass) đạt, $($script:fail) lỗi"
if ($script:fail -eq 0) { "TẤT CẢ ĐẠT" } else { "CÓ $($script:fail) MỤC LỖI" }
""
"Dọn đơn thử (chạy trong pgAdmin để trả lại ghế):"
"DELETE FROM `"Bookings`" WHERE `"Code`" IN ('$code1', '$code2');"