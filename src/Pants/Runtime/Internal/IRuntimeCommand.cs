namespace Cntryl.Pants.Runtime.Internal;

interface IRuntimeCommand
{
    ValueTask ExecuteAsync(RuntimeState state);
}
