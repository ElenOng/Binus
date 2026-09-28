using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Binus.Controllers;

public class AccountController : Controller
{
    private readonly Binus.DataAccess.IUserRepository _userRepository;

    public AccountController(Binus.DataAccess.IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }
    // GET: /Account/Login
    [HttpGet]
    public IActionResult Login(string returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    // POST: /Account/Login
    [HttpPost]
    public async Task<IActionResult> Login(string binusianId, string password, string returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        // Validate against database using ADO.NET and get display name
        var displayName = await _userRepository.ValidateCredentialsAndGetDisplayNameAsync(binusianId, password);
        if (!string.IsNullOrEmpty(binusianId) && displayName != null)
        {
            var claims = new List<Claim>
            {
                // Set ClaimTypes.Name to the display name so User.Identity.Name shows the employee name
                new Claim(ClaimTypes.Name, displayName),
                new Claim("BinusianId", binusianId)
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(claimsIdentity);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToAction("Registration", "Home");
        }

        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login", "Account");
    }
}
