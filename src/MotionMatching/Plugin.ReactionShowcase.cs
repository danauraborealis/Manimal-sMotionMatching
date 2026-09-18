using UnityEngine;

namespace Manimal.MotionMatching
{
    public sealed partial class Plugin
    {
        private int _showcaseStep = -1;
        private float _showcaseStepAt;
        private bool _showcasePlayed;
        private void TickReactionShowcase()
        {
            if (_puppet == null || _puppet.Scenario != "reactionshowcase" || _pose == null || !_reactionsEnabled) return;
            int step = _puppet.StepIndex;
            if (step != _showcaseStep)
            { _showcaseStep = step; _showcaseStepAt = Time.time; _showcasePlayed = false; }
            if (_showcasePlayed || Time.time - _showcaseStepAt < 1f) return;
            string clip = null;
            switch (step)
            {
                case 0: clip = "new_flinch_02_hitreact"; break;
                case 1: clip = "new_flinch_35_hitreact"; break;
                case 2: clip = "new_flinch_06_hitreact"; break;
                case 3: clip = "new_flinch_20_hitreact"; break;
                case 4: clip = "new_flinch_11_hitreact"; break;
                case 5: clip = "stumble_n"; break;
                case 7: clip = "stumble_s"; break;
                case 9: clip = "stumble_e"; break;
                case 11: clip = "stumble_w"; break;
            }
            if (clip == null) return;
            _reactionNote = "SHOWCASE: " + clip;
            _showcasePlayed = _pose.PreviewReaction(clip);
        }
    }
}
