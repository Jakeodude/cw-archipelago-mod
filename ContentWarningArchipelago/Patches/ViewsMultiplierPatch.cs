// Patches/ViewsMultiplierPatch.cs
//
// REMOVED: The BigNumbers.GetScoreToViews patch that previously lived here was
// incorrectly increasing quota difficulty.  BigNumbers.GetScoreToViews is called
// by both UI_Views.Update() (quota progress display) and
// ContentBuffer.GenerateComments() (uploaded-footage view counts).  Patching it
// globally multiplied both, making the required-views target appear higher and
// effectively raising quota difficulty instead of buffing the player.
//
// REPLACEMENT: The footage view multiplier is now applied inside
// ContentEvaluatorPatch.FilmingPostfix (ItemPickupPatch.cs).  After
// ContentEvaluator.EvaluateRecording returns, every BufferedContent.score in the
// ContentBuffer is multiplied by (1.0 + viewsMultiplierLevel × 0.1) before
// GenerateComments converts scores to view counts.  This scopes the buff
// exclusively to extracted footage and leaves the quota display unchanged.
