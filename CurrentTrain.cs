using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
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
            var job = JobData.JobForCar(car);
            var destination = job == null ? null : FinalDestination(job);

            return new JObject(
                new JProperty("inTrain", true),
                new JProperty("carId", car.ID),
                new JProperty("trainsetId", trainset == null ? -1 : trainset.id),
                new JProperty("jobId", job?.ID),
                new JProperty("jobType", job?.jobType.ToString()),
                new JProperty("destinationYardId", job?.chainData?.chainDestinationYardId),
                new JProperty("destinationTrack", destination));
        }

        /// The last track the job asks the cars to be placed on. Tasks nest, so
        /// this walks the tree and takes the final destination it finds.
        private static string? FinalDestination(Job job)
        {
            if (job.tasks == null)
                return null;
            string? last = null;
            foreach (var task in job.tasks)
            {
                foreach (var data in Flatten(task.GetTaskData()))
                {
                    var track = data.destinationTrack;
                    if (track != null)
                        last = track.ID.FullDisplayID;
                }
            }
            return last;
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
