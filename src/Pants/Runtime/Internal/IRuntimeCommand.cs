namespace Pants;

internal interface IRuntimeCommand
{
    ValueTask ExecuteAsync(PantsRuntimeState state);
}
