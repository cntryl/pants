namespace Cntryl.Pants;

internal interface IRuntimeCommand
{
    ValueTask ExecuteAsync(PantsRuntimeState state);
}
