using System;
using EFT;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // pulls a bot's brain agent out of the AI controller so its layers stop issuing orders.
    // the brain object itself is never destroyed; it goes back in only if it's still intact
    internal sealed class BrainLease
    {
        private readonly BotOwner _owner;
        private bool _removed;

        public BrainLease(BotOwner owner, string rejectPrefix)
        {
            if (owner.Brain == null || owner.Brain.Agent == null || owner.Brain.BaseBrain == null)
                throw new InvalidOperationException(rejectPrefix + "bot brain is not initialized.");
            if (owner.BotsController == null || owner.BotsController.AICoreController == null)
                throw new InvalidOperationException(rejectPrefix + "bot AI controller is unavailable.");
            var registered = owner.BotsController.AICoreController.ListAgents();
            if (registered == null || !registered.Contains(owner.Brain.Agent))
                throw new InvalidOperationException(rejectPrefix + "bot brain agent is not registered.");
            if (owner.Brain.Agent._aiCoreController != owner.BotsController.AICoreController)
                throw new InvalidOperationException(rejectPrefix + "bot brain agent belongs to another AI controller.");
            if (owner.Brain._owner != owner)
                throw new InvalidOperationException(rejectPrefix + "bot brain owner does not match the selected bot.");

            _owner = owner;
            Brain = owner.Brain;
            Agent = owner.Brain.Agent;
            Controller = owner.BotsController.AICoreController;
        }

        public StandartBotBrain Brain { get; }
        public AICoreAgent<BotLogicDecision> Agent { get; }
        public AICoreController Controller { get; }

        public bool IsIntact => _owner.Brain == Brain && Brain.Agent == Agent && Brain._owner == _owner;

        public bool IsReRegistered => Controller.ListAgents().Contains(Agent);

        public void Acquire(string rejectPrefix)
        {
            if (!Controller.ListAgents().Remove(Agent))
                throw new InvalidOperationException(rejectPrefix + "could not pause the registered brain agent.");
            _removed = true;
        }

        public void Release(bool ownerUsable)
        {
            if (!_removed || !ownerUsable)
                return;
            try
            {
                if (!IsIntact || Agent._aiCoreController != Controller || Agent._strategy == null || Agent._nodesDictionary == null)
                {
                    Debug.LogWarning("[" + ModInfo.Name + "] Brain was changed or disposed; leaving its agent detached.");
                    return;
                }
                Controller.AddNodeController(Agent);
                _removed = false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[" + ModInfo.Name + "] Could not restore the vanilla brain agent: " + ex);
            }
        }
    }
}
