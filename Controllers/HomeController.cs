using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProxyCraft.Api.Documentation;
using ProxyCraft.Api.Contracts;
using ProxyCraft.Services;

namespace ProxyCraft.Controllers;

public sealed class HomeController(
    ApiReferenceService reference,
    SettingsStore settings,
    PublicDashboardService dashboard) : Controller
{
    [AllowAnonymous, HttpGet("/")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (!settings.Current.AuthenticationEnabled || User.Identity?.IsAuthenticated == true)
        {
            return View();
        }

        PublicDashboardResponse? response = await dashboard.GetAsync(cancellationToken);
        return response is null ? Challenge() : View("Welcome", response);
    }

    [HttpGet("/proxies")]
    public IActionResult Proxies() => View();

    [HttpGet("/settings/proxy")]
    public IActionResult ProxySettings() => View();

    [HttpGet("/settings/app")]
    public IActionResult AppSettings() => View();

    [HttpGet("/settings/api")]
    public IActionResult Api() => View(reference.List());

    [AllowAnonymous, Route("/error"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Error(int? statusCode)
    {
        Response.StatusCode = statusCode ?? StatusCodes.Status500InternalServerError;
        ViewData["StatusCode"] = Response.StatusCode;
        return View();
    }
}
