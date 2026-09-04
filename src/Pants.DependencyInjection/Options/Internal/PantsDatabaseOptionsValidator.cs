using Cntryl.Pants.Exceptions;
using Microsoft.Extensions.Options;

namespace Cntryl.Pants.Options.Internal;

sealed class PantsDatabaseOptionsValidator : IValidateOptions<PantsDatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, PantsDatabaseOptions options)
    {
        try
        {
            _ = PantsDatabaseOptionsMapper.Create(options);
            return ValidateOptionsResult.Success;
        }
        catch (Exception exception) when (exception is PantsException or
                                              ArgumentException or
                                              InvalidOperationException or
                                              OverflowException)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
