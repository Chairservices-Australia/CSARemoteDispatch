using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV.RemoteControls;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Holds trains at a red aspect so an occupied block is not entered and the
    /// main line stays clear until it resolves.
    ///
    /// Only consists this mod is already driving are held: a train under a
    /// dispatcher route, or one being driven from the web console. The player's
    /// own train is left alone unless they ask for it, because taking the brake
    /// off a driver without warning is worse than any block it protects.
    public static class SignalEnforcement
    {
        public const float TickSeconds = 0.25f;

        /// Below this speed the train counts as stopped and the brake is held
        /// rather than reapplied, so it settles instead of oscillating.
        private const float StoppedSpeed = 0.3f;

        /// Consists currently held, and the brake value applied, so a hold is
        /// only released by whoever applied it.
        private static readonly Dictionary<int, float> held = new Dictionary<int, float>();

        public static bool IsHeld(int trainsetId) => held.ContainsKey(trainsetId);

        public static IEnumerator EnforceCoroutine()
        {
            var wait = new WaitForSeconds(TickSeconds);
            while (true)
            {
                yield return wait;
                if (!Main.settings.enforceSignals)
                {
                    ReleaseAll();
                    continue;
                }
                try
                {
                    Tick();
                }
                catch (System.Exception e)
                {
                    Main.DebugLog(() => $"Signal enforcement error: {e.Message}");
                }
            }
        }

        private static void Tick()
        {
            var sets = Trainset.allSets;
            if (sets == null)
                return;

            var stillHeld = new HashSet<int>();
            foreach (var trainset in sets)
            {
                if (trainset == null || trainset.cars == null || trainset.cars.Count == 0)
                    continue;
                if (!ShouldEnforce(trainset))
                    continue;

                var reading = Signalling.ReadFor(trainset.cars[0]);
                if (reading.aspect == Aspect.Stop)
                {
                    Hold(trainset);
                    stillHeld.Add(trainset.id);
                }
                else if (held.ContainsKey(trainset.id))
                {
                    Release(trainset);
                }
            }

            // Consists that vanished while held.
            foreach (var id in held.Keys.Where(id => !stillHeld.Contains(id)).ToList())
                held.Remove(id);
        }

        /// A train is held when the dispatcher has given it a road, or when the
        /// player has opted in for the train they are aboard.
        private static bool ShouldEnforce(Trainset trainset)
        {
            if (Routing.AllRoutes().Any(route => route.trainsetId == trainset.id))
                return true;
            if (!Main.settings.enforceSignalsForPlayerTrain)
                return false;
            var playerCar = PlayerManager.Car;
            return playerCar != null && playerCar.trainset != null
                && playerCar.trainset.id == trainset.id;
        }

        private static void Hold(Trainset trainset)
        {
            var speed = SpeedOf(trainset);
            // Full brake while still rolling, easing to a holding application
            // once stopped so the train stands rather than fighting itself.
            var brake = speed > StoppedSpeed ? 1f : 0.6f;

            foreach (var controller in ControllersOf(trainset))
            {
                controller.controlsOverrider.Throttle?.Set(0f);
                controller.controlsOverrider.Brake?.Set(brake);
                controller.controlsOverrider.IndependentBrake?.Set(brake);
            }
            held[trainset.id] = brake;
        }

        private static void Release(Trainset trainset)
        {
            foreach (var controller in ControllersOf(trainset))
            {
                controller.controlsOverrider.Brake?.Set(0f);
                controller.controlsOverrider.IndependentBrake?.Set(0f);
            }
            held.Remove(trainset.id);
        }

        private static void ReleaseAll()
        {
            if (held.Count == 0)
                return;
            var sets = Trainset.allSets;
            if (sets != null)
            {
                foreach (var trainset in sets.Where(t => t != null && held.ContainsKey(t.id)).ToList())
                    Release(trainset);
            }
            held.Clear();
        }

        private static IEnumerable<RemoteControllerModule> ControllersOf(Trainset trainset)
        {
            foreach (var car in trainset.cars)
            {
                if (car == null)
                    continue;
                var controller = car.GetComponent<RemoteControllerModule>();
                if (controller != null && controller.controlsOverrider != null)
                    yield return controller;
            }
        }

        private static float SpeedOf(Trainset trainset)
        {
            var car = trainset.firstCar ?? trainset.cars.FirstOrDefault(c => c != null);
            if (car == null || car.rb == null)
                return 0f;
            return car.rb.velocity.magnitude;
        }
    }
}
