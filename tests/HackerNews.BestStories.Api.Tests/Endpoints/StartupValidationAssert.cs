using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace HackerNews.BestStories.Api.Tests.Endpoints;

internal static class StartupValidationAssert
{
    public static void ThrowsOptionsValidation(Func<WebApplicationFactory<Program>> factoryFactory)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var factory = factoryFactory();
            try
            {
                using var client = factory.CreateClient();
            }
            catch (OptionsValidationException)
            {
                return;
            }
            catch (ObjectDisposedException ex) when (attempt < 2)
            {
                last = ex;
                continue;
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }

            Assert.Fail("Expected startup validation to fail.");
        }

        Assert.Fail($"Expected {nameof(OptionsValidationException)}, but got {last?.GetType().Name}: {last}");
    }
}
