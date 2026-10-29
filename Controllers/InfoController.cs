using CinemaApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace CinemaApp.Controllers;

// Các trang thông tin và chính sách: /Info/faq, /Info/refund, /Info/terms, /Info/privacy, /Info/ratings, /Info/about, /Info/contact
public class InfoController : Controller
{
    [HttpGet("Info/{slug}")]
    public IActionResult Page(string slug)
    {
        var page = InfoPages.Find(slug);
        if (page == null) return NotFound();
        return View("Page", page);
    }
}