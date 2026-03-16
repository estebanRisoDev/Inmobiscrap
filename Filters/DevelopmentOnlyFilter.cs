using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Inmobiscrap.Filters;

public class DevelopmentOnlyFilter : IActionFilter
{
    private readonly IWebHostEnvironment _env;

    public DevelopmentOnlyFilter(IWebHostEnvironment env) => _env = env;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!_env.IsDevelopment())
        {
            context.Result = new NotFoundResult();
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
