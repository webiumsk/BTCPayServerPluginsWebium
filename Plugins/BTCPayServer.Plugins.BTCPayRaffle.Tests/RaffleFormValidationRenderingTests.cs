using System.Diagnostics;
using System.Reflection;
using System.Text.Encodings.Web;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using BTCPayServer.Plugins.BTCPayRaffle.ViewModels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace BTCPayServer.Plugins.BTCPayRaffle.Tests;

public class RaffleFormValidationRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_create_and_edit_fields_display_description_errors(bool edit)
    {
        var assembly = typeof(RaffleService).Assembly;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IWebHostEnvironment>(new TestEnvironment());
        services.AddSingleton<DiagnosticListener>(new DiagnosticListener("raffle-form-test"));
        services.AddSingleton<DiagnosticSource>(provider => provider.GetRequiredService<DiagnosticListener>());
        services.AddSingleton(HostRichTextPolicy.Create());
        services.AddSingleton<RaffleStringLocalizer>();
        services.AddControllersWithViews().AddApplicationPart(assembly);
        using var provider = services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = provider };
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var state = new ModelStateDictionary();
        const string error = "Description must not exceed 8000 characters after sanitization.";
        state.AddModelError(nameof(CreateEditRaffleViewModel.Description), error);
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor(), state);
        var model = new CreateEditRaffleViewModel
        {
            RaffleId = edit ? Guid.NewGuid() : null,
            Status = edit ? RaffleStatus.Draft : null,
            Name = "Description validation",
            Description = "<p>Existing description</p>",
            CanEditPricing = false,
            CanEditMaxTickets = false
        };
        var data = new ViewDataDictionary<CreateEditRaffleViewModel>(
            provider.GetRequiredService<IModelMetadataProvider>(), state) { Model = model };
        var item = assembly.GetCustomAttributes<RazorCompiledItemAttribute>()
            .Single(x => x.Identifier == "/Views/UIRaffle/_RaffleFormFields.cshtml");
        var page = (RazorPage<CreateEditRaffleViewModel>)Activator.CreateInstance(item.Type, nonPublic: true)!;
        using var writer = new StringWriter();
        var context = new ViewContext(action, new EmptyView(), data,
            new TempDataDictionary(http, new EmptyTempData()), writer, new HtmlHelperOptions());
        page.ViewContext = context;
        page.Path = item.Identifier;
        page.HtmlEncoder = HtmlEncoder.Default;
        provider.GetRequiredService<IRazorPageActivator>().Activate(page, context);
        await page.ExecuteAsync();

        var html = writer.ToString();
        Assert.Contains("data-valmsg-for=\"Description\"", html);
        Assert.Contains(error, html);
        Assert.Contains("field-validation-error", html);
        Assert.Contains("name=\"Description\"", html);
    }

    private sealed class EmptyView : IView
    {
        public string Path => "/Views/UIRaffle/_RaffleFormFields.cshtml";
        public Task RenderAsync(ViewContext context) => Task.CompletedTask;
    }

    private sealed class EmptyTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = typeof(RaffleService).Assembly.GetName().Name!;
        public string EnvironmentName { get; set; } = "Testing";
        public string WebRootPath { get; set; } = System.IO.Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = System.IO.Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
