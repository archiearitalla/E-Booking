using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BarberBook.Web.Pages.Admin;

public class AboutModel(IWebHostEnvironment environment) : AdminPageModel
{
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public string EnvironmentName { get; } = environment.EnvironmentName;

    public void OnGet() { }
}
