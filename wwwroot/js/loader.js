(function () {
  // ---- CSS tự chứa (tiền tố sl-) ----
  var css =
    "" +
    ".sl-ov{position:fixed;inset:0;z-index:99999;display:flex;align-items:center;justify-content:center;" +
    "background:rgba(10,8,6,.72);backdrop-filter:blur(3px);opacity:0;visibility:hidden;transition:opacity .2s,visibility .2s}" +
    ".sl-ov.on{opacity:1;visibility:visible}" +
    ".sl-box{display:flex;flex-direction:column;align-items:center;gap:14px}" +
    ".sl-ring{position:relative;width:64px;height:64px}" +
    '.sl-ring::before{content:"";position:absolute;inset:0;border-radius:50%;border:3px solid rgba(230,162,60,.18);' +
    "border-top-color:#e6a23c;border-right-color:#ffd27a;animation:sl-spin .9s linear infinite}" +
    ".sl-ring svg{position:absolute;inset:18px;width:28px;height:28px;animation:sl-pulse 1.4s ease-in-out infinite}" +
    ".sl-txt{font-size:13px;letter-spacing:2px;color:#ffd27a;font-weight:700}" +
    "@keyframes sl-spin{to{transform:rotate(360deg)}}" +
    "@keyframes sl-pulse{0%,100%{transform:scale(.85);opacity:.7}50%{transform:scale(1.1);opacity:1}}";
  var st = document.createElement("style");
  st.textContent = css;
  document.head.appendChild(st);

  // ---- Lớp phủ ----
  var ov = document.createElement("div");
  ov.className = "sl-ov";
  ov.setAttribute("aria-hidden", "true");
  ov.innerHTML =
    '<div class="sl-box"><div class="sl-ring">' +
    '<svg viewBox="0 0 24 24"><polygon points="12,2 14.9,8.6 22,9.3 16.6,14 18.2,21 12,17.3 5.8,21 7.4,14 2,9.3 9.1,8.6" fill="#e6a23c"/></svg>' +
    '</div><div class="sl-txt">ĐANG TẢI...</div></div>';

  var showTimer = null,
    safeTimer = null;

  function show() {
    clearTimeout(showTimer);
    showTimer = setTimeout(function () {
      ov.classList.add("on");
      clearTimeout(safeTimer);
      safeTimer = setTimeout(hide, 15000); // chốt an toàn
    }, 250);
  }
  function hide() {
    clearTimeout(showTimer);
    clearTimeout(safeTimer);
    ov.classList.remove("on");
  }
  window.StarLoader = { show: show, hide: hide };

  function init() {
    document.body.appendChild(ov);
  }
  if (document.body) init();
  else document.addEventListener("DOMContentLoaded", init);

  // ---- Bấm liên kết nội bộ ----
  document.addEventListener("click", function (e) {
    var a = e.target.closest ? e.target.closest("a") : null;
    if (!a || e.defaultPrevented) return;
    if (e.ctrlKey || e.metaKey || e.shiftKey || e.altKey || e.button !== 0)
      return;
    var href = a.getAttribute("href");
    if (
      !href ||
      href.charAt(0) === "#" ||
      /^(javascript|mailto|tel):/i.test(href)
    )
      return;
    if (a.target && a.target !== "_self") return;
    if (
      a.hasAttribute("download") ||
      a.hasAttribute("data-bs-toggle") ||
      a.hasAttribute("data-no-loader")
    )
      return;
    if (a.origin !== location.origin) return;
    if (
      a.pathname === location.pathname &&
      a.search === location.search &&
      a.hash
    )
      return; // chỉ nhảy neo
    show();
  });

  // ---- Gửi form ----
  document.addEventListener("submit", function (e) {
    var f = e.target;
    if (!f || f.hasAttribute("data-no-loader")) return;
    // đợi các bộ kiểm tra khác (validation) xử lý xong, nếu không bị chặn mới hiện
    setTimeout(function () {
      if (!e.defaultPrevented) show();
    }, 0);
  });

  // ---- Tắt khi quay lại bằng nút Back (trang lấy từ bộ nhớ đệm) ----
  window.addEventListener("pageshow", hide);
})();
