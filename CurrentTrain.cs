using System.Collections.Generic;
using DV.Logic.Job;
using DV.ThingTypes;
using Newtonsoft.Json.Linq;

namespace DvMod.RemoteDispatch
{
    /// The consist the player is currently riding in, and the job it is working.
    ///
    /// Lets the dispatcher page preselect the train and its booked destination,
    /// so being aboard and pressing Set route is enough.
    public static class CurrentTrain
    {
        public static JObject GetCurrentTrainJson()
        {
            var car = PlayerManager.Car;
            if (car == null)
                return new JObject(new JProperty("inTrain", false));

            var trainset = car.trainset;
            // Everything on the drawbar, not just the car the driver is in: a
            // locomotive is never one of a job's cars, so the cab alone reports
            // nothing once the train is coupled up. The cab's own job is still
            // consulted as a fallback, for a driver riding in a job's caboose
            // with the registry somehow not naming it.
            var jobs = JobData.JobsForTrainset(trainset);
            var job = jobs.Count > 0 ? jobs[0] : JobData.JobForCar(car);
            var occupied = new HashSet<RailTrack>();
            Occupancy.TracksOccupiedBy(trainset, occupied);
            var stops = job == null ? new List<string>() : StopsFor(job, occupied);

            var jobsJson = new JArray();
            foreach (var candidate in jobs)
            {
                jobsJson.Add(new JObject(
                    new JProperty("id", candidate.ID),
                    new JProperty("type", candidate.jobType.ToString()),
                    new JProperty("isActive", candidate.State == JobState.InProgress),
                    new JProperty("cars", JobData.CarsOfJobIn(candidate, trainset)),
                    new JProperty("destinationYardId", candidate.chainData?.chainDestinationYardId),
                    new JProperty("stops", new JArray(StopsFor(candidate, occupied)))));
            }

            return new JObject(
                new JProperty("inTrain", true),
                new JProperty("carId", car.ID),
                new JProperty("carGuid", car.CarGUID),
                new JProperty("trainsetId", trainset == null ? -1 : trainset.id),
                new JProperty("jobId", job?.ID),
                new JProperty("jobType", job?.jobType.ToString()),
                new JProperty("destinationYardId", job?.chainData?.chainDestinationYardId),
                // The end of the road, kept as its own field: choosing a train
                // and pressing Set route still books the final destination
                // without touching the itinerary.
                new JProperty("destinationTrack", stops.Count > 0 ? stops[stops.Count - 1] : null),
                new JProperty("stops", new JArray(stops)),
                new JProperty("jobs", jobsJson));
        }

        /// Everywhere a job's movements call, in the order they call there -
        /// the itinerary a road should follow.
        ///
        /// Both ends of each movement count. A shunting job's pickup roads are
        /// where its tasks start, not where they finish, so taking only the
        /// destinations would name the loading track and none of the places the
        /// cars have to be collected from first.
        ///
        /// Consecutive repeats are dropped: one task ends where the next begins,
        /// and a task that loads or unloads names the road the movement before
        /// it already reached. A call at the place you are already standing is a
        /// leg of no length. Repeats that are not consecutive stay, because
        /// coming back to the loading road between cuts is the move.
        private static List<string> StopsFor(Job job, HashSet<RailTrack> occupied)
        {
            var stops = new List<string>();
            if (job.tasks == null)
                return stops;
            foreach (var task in job.tasks)
            {
                foreach (var data in Flatten(task.GetTaskData()))
                {
                    AddStop(stops, data.startTrack);
                    AddStop(stops, data.destinationTrack);
                }
            }

            // Coupling up at the first station is exactly when this is asked
            // for, and the road the train is already standing on is not
            // somewhere it needs a route to. The last call is never dropped: it
            // is the destination even if the train is somehow already there.
            while (stops.Count > 1 && StandingOn(stops[0], occupied))
                stops.RemoveAt(0);
            return stops;
        }

        private static void AddStop(List<string> stops, Track? track)
        {
            if (track == null)
                return;
            var id = track.ID.FullDisplayID;
            if (stops.Count > 0 && stops[stops.Count - 1] == id)
                return;
            stops.Add(id);
        }

        private static bool StandingOn(string trackId, HashSet<RailTrack> occupied)
        {
            foreach (var track in Routing.FindTracks(trackId))
            {
                if (occupied.Contains(track))
                    return true;
            }
            return false;
        }

        private static IEnumerable<TaskData> Flatten(TaskData data)
        {
            if (data.nestedTasks != null && data.nestedTasks.Count > 0)
            {
                foreach (var nested in data.nestedTasks)
                {
                    foreach (var inner in Flatten(nested.GetTaskData()))
                        yield return inner;
                }
                yield break;
            }
            yield return data;
        }
    }
}
