using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AxiomLedger.Web.Pages;

public sealed class StatusModel : PageModel
{
    public int Code { get; private set; } = 404;
    public string Title { get; private set; } = "Page not found";
    public string Message { get; private set; } = "The page you were looking for does not exist.";

    public void OnGet(int? code)
    {
        Code = code ?? 404;
        (Title, Message) = Code switch
        {
            403 => ("Access denied", "Your role does not allow you to open this page."),
            404 => ("Page not found", "The page you were looking for does not exist or has moved."),
            400 => ("That did not work", "The request was not valid. Go back and try again."),
            500 => ("Something went wrong", "An unexpected error occurred. The details were written to the server log."),
            _ => ("Unexpected response", "The server returned an unexpected status.")
        };
    }
}
