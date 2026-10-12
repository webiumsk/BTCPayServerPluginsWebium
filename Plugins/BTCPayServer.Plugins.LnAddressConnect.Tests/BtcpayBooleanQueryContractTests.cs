using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace BTCPayServer.Plugins.LnAddressConnect.Tests;

public class BtcpayBooleanQueryContractTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("1", false)]
    [InlineData("0", false)]
    public async Task IncludeArchived_uses_the_actual_AspNet_boolean_binder(string value, bool valid)
    {
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var values = new QueryStringValueProvider(BindingSource.Query,
            new QueryCollection(new Dictionary<string, StringValues> { ["includeArchived"] = value }), CultureInfo.InvariantCulture);
        var context = DefaultModelBindingContext.CreateBindingContext(action, values,
            new EmptyModelMetadataProvider().GetMetadataForType(typeof(bool)), null, "includeArchived");
        await new SimpleTypeModelBinder(typeof(bool), NullLoggerFactory.Instance).BindModelAsync(context);
        Assert.Equal(valid, context.ModelState.ErrorCount == 0);
        Assert.Equal(valid, context.Result.IsModelSet);
    }
}
