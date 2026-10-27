using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using Godot;

namespace Sts2.ParityMod;

/// <summary>
/// Entry point for the parity recording mod. ModManager calls <see cref="Init"/> once it has
/// loaded this assembly, on the Godot main thread, while NGame is still starting up.
/// </summary>
[ModInitializer(nameof(Init))]
public static class ParityMod
{
    private static IDisposable? _selectorScope;

    public static void Init()
    {
        try
        {
            // Make the game skip animation delays and interactive waits. This is the same hook
            // AutoSlay uses; it is not TestMode, so model/combat behavior is untouched.
            NonInteractiveMode.AutoSlayerCheck = () => true;
            // Headless parity runs cannot show the card-selection screen. Install a deterministic
            // selector (the same "first eligible card" policy the engine implements) so effects that
            // ask the player to pick a card (Survivor's discard, etc.) resolve reproducibly.
            _selectorScope = CardSelectCmd.PushSelector(new FirstCardSelector());

            var tree = (SceneTree)Engine.GetMainLoop();
            Log.Info("[parity] initializer; sync context=" + (SynchronizationContext.Current?.GetType().FullName ?? "<null>"));
            var driver = new ParityDriver(tree);
            _ = driver.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error("[parity] initializer failed: " + ex);
        }
    }
}

/// <summary>Selects the first eligible cards in list order; the engine mirrors this policy.</summary>
internal sealed class FirstCardSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
{
    public Task<IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>> GetSelectedCards(
        IEnumerable<MegaCrit.Sts2.Core.Models.CardModel> options, int minSelect, int maxSelect)
    {
        int take = Math.Clamp(minSelect <= 0 ? 1 : minSelect, 0, maxSelect <= 0 ? 1 : maxSelect);
        return Task.FromResult<IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>>(options.Take(take).ToList());
    }

    public MegaCrit.Sts2.Core.TestSupport.CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative> alternatives)
        => throw new NotSupportedException("card rewards are outside the combat harness");
}
