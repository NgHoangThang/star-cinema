(function () {
  var reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // Thanh menu đậm lên khi cuộn
  var nav = document.getElementById("st-nav");
  function onScroll() {
    if (nav) nav.classList.toggle("scrolled", window.scrollY > 8);
  }
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  // Hiện dần khi cuộn tới
  var items = document.querySelectorAll(".st-reveal");
  if ("IntersectionObserver" in window && !reduce) {
    var io = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (e) {
          if (e.isIntersecting) {
            e.target.classList.add("in");
            io.unobserve(e.target);
          }
        });
      },
      { threshold: 0.12 },
    );
    items.forEach(function (el) {
      io.observe(el);
    });
  } else {
    items.forEach(function (el) {
      el.classList.add("in");
    });
  }

  // Thẻ nghiêng 3D theo con trỏ (chỉ máy có chuột)
  if (window.matchMedia("(hover: hover)").matches && !reduce) {
    document.querySelectorAll(".st-tilt").forEach(function (el) {
      el.addEventListener("pointermove", function (e) {
        var r = el.getBoundingClientRect();
        var x = (e.clientX - r.left) / r.width;
        var y = (e.clientY - r.top) / r.height;
        el.style.setProperty("--ry", (x - 0.5) * 14 + "deg");
        el.style.setProperty("--rx", (0.5 - y) * 14 + "deg");
        el.style.setProperty("--mx", x * 100 + "%");
        el.style.setProperty("--my", y * 100 + "%");
      });
      el.addEventListener("pointerleave", function () {
        el.style.setProperty("--rx", "0deg");
        el.style.setProperty("--ry", "0deg");
      });
    });
  }

  // Số đếm tăng dần
  document.querySelectorAll("[data-count]").forEach(function (el) {
    var target = parseInt(el.getAttribute("data-count"), 10) || 0;
    if (reduce || target === 0) {
      el.textContent = target;
      return;
    }
    var start = null;
    function step(ts) {
      if (start === null) start = ts;
      var p = Math.min((ts - start) / 900, 1);
      el.textContent = Math.round(target * (1 - Math.pow(1 - p, 3)));
      if (p < 1) requestAnimationFrame(step);
    }
    requestAnimationFrame(step);
  });

  // Ẩn / hiện mật khẩu
  document.querySelectorAll("[data-toggle-pw]").forEach(function (b) {
    b.addEventListener("click", function () {
      var inp = b.parentElement.querySelector("input");
      if (!inp) return;
      var show = inp.type === "password";
      inp.type = show ? "text" : "password";
      b.classList.toggle("on", show);
    });
  });

  // Ô nhập mã 6 số: gõ tự nhảy ô, xóa lùi được, dán cả mã được
  var firstOtp = true;
  document.querySelectorAll("[data-otp]").forEach(function (wrap) {
    var hidden = document.querySelector(wrap.getAttribute("data-otp"));
    var boxes = wrap.querySelectorAll(".sa-digit");
    var form = wrap.closest("form");

    function sync() {
      if (hidden)
        hidden.value = Array.prototype.map
          .call(boxes, function (b) {
            return b.value;
          })
          .join("");
    }

    if (hidden && hidden.value) {
      hidden.value
        .replace(/\D/g, "")
        .slice(0, 6)
        .split("")
        .forEach(function (ch, k) {
          boxes[k].value = ch;
        });
    }

    boxes.forEach(function (b, i) {
      b.addEventListener("input", function () {
        b.value = b.value.replace(/\D/g, "").slice(-1);
        sync();
        if (b.value && i < boxes.length - 1) boxes[i + 1].focus();
      });
      b.addEventListener("keydown", function (e) {
        if (e.key === "Backspace" && !b.value && i > 0) boxes[i - 1].focus();
        if (e.key === "ArrowLeft" && i > 0) boxes[i - 1].focus();
        if (e.key === "ArrowRight" && i < boxes.length - 1)
          boxes[i + 1].focus();
      });
      b.addEventListener("paste", function (e) {
        e.preventDefault();
        var text = (e.clipboardData || window.clipboardData)
          .getData("text")
          .replace(/\D/g, "")
          .slice(0, 6);
        text.split("").forEach(function (ch, k) {
          boxes[k].value = ch;
        });
        sync();
        boxes[Math.min(text.length, boxes.length - 1)].focus();
      });
    });

    if (form) {
      form.addEventListener("submit", function (e) {
        sync();
        if (hidden && hidden.value.length !== 6) {
          e.preventDefault();
          wrap.classList.add("shake");
          setTimeout(function () {
            wrap.classList.remove("shake");
          }, 450);
          boxes[0].focus();
        }
      });
    }
    sync();
    if (firstOtp) {
      boxes[0].focus();
      firstOtp = false;
    }
  });

  // Nút gửi lại có đếm ngược
  document.querySelectorAll("[data-countdown]").forEach(function (btn) {
    var left = parseInt(btn.getAttribute("data-countdown"), 10) || 60;
    var label = btn.getAttribute("data-label") || btn.textContent.trim();
    btn.disabled = true;
    btn.textContent = label + " (" + left + "s)";
    var timer = setInterval(function () {
      left--;
      if (left <= 0) {
        clearInterval(timer);
        btn.disabled = false;
        btn.textContent = label;
      } else {
        btn.textContent = label + " (" + left + "s)";
      }
    }, 1000);
  });
})();
