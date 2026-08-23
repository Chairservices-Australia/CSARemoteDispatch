using DV.Logic.Job;
using DV.ThingTypes.TransitionHelpers;
using DV.ThingTypes;
using DV.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    public static class JobData
    {
        private static readonly Dictionary<TrainCar, string> jobIdForCar = InitializeJobIdForCar();
        private static Dictionary<string, Job> jobForId = new Dictionary<string, Job>();

        private const JobLicenses LicensesToExport =
          JobLicenses.Hazmat1 | JobLicenses.Hazmat2 | JobLicenses.Hazmat3 |
          JobLicenses.Military1 | JobLicenses.Military2 | JobLicenses.Military3 |
          JobLicenses.TrainLength1 | JobLicenses.TrainLength2;

        public static string? JobIdForCar(TrainCar car)
        {
            jobIdForCar.TryGetValue(car, out var jobId);
            return jobId;
        }

        public static Job? JobForCar(TrainCar car)
        {
            var jobId = JobIdForCar(car);
            if (jobId == null)
                return null;
            return JobForId(jobId);
        }

        /// Every job with at least one of its cars in this consist, most
        /// relevant first.
        ///
        /// "What is this train working" is not the same question as "what job is
        /// the driver's car on". A locomotive is never one of a job's cars, so
        /// asking about the car the driver sits in reports no job at all the
        /// moment the train is coupled up and ready to leave - which is exactly
        /// when the answer is wanted.
        ///
        /// Read from the live job registry rather than the per-car index, which
        /// is filled by a patch on the game's own plate update and can therefore
        /// be missing a job that was set up before this mod loaded.
        public static List<Job> JobsForTrainset(Trainset? trainset)
        {
            var result = new List<Job>();
            var cars = trainset?.cars;
            if (cars == null || cars.Count == 0)
                return result;
            var manager = SingletonBehaviour<JobsManager>.Instance;
            if (manager == null || manager.jobToJobCars == null)
                return result;

            var inConsist = new HashSet<TrainCar>();
            foreach (var car in cars)
            {
                if (car != null)
                    inConsist.Add(car);
            }

            var carsHere = new Dictionary<Job, int>();
            foreach (var pair in manager.jobToJobCars)
            {
                var job = pair.Key;
                if (job == null || pair.Value == null)
                    continue;
                var matched = 0;
                foreach (var car in pair.Value)
                {
                    var trainCar = car?.TrainCar();
                    if (trainCar != null && inConsist.Contains(trainCar))
                        matched++;
                }
                if (matched > 0)
                    carsHere[job] = matched;
            }

            result.AddRange(carsHere.Keys);
            // Taken jobs first, then whichever has most of its cars on the
            // drawbar, then by ID - so the order is the same from one poll to
            // the next rather than however the registry happened to enumerate.
            result.Sort((a, b) =>
            {
                var takenA = a.State == JobState.InProgress ? 0 : 1;
                var takenB = b.State == JobState.InProgress ? 0 : 1;
                if (takenA != takenB)
                    return takenA - takenB;
                if (carsHere[a] != carsHere[b])
                    return carsHere[b] - carsHere[a];
                return string.CompareOrdinal(a.ID, b.ID);
            });
            return result;
        }

        /// How many of a job's cars are in this consist.
        public static int CarsOfJobIn(Job job, Trainset? trainset)
        {
            var cars = trainset?.cars;
            var manager = SingletonBehaviour<JobsManager>.Instance;
            if (cars == null || job == null || manager?.jobToJobCars == null
                || !manager.jobToJobCars.TryGetValue(job, out var jobCars) || jobCars == null)
                return 0;
            var matched = 0;
            foreach (var car in jobCars)
            {
                var trainCar = car?.TrainCar();
                if (trainCar != null && cars.Contains(trainCar))
                    matched++;
            }
            return matched;
        }

        private static Dictionary<TrainCar, string> InitializeJobIdForCar()
        {
            return SingletonBehaviour<JobsManager>.Instance.jobToJobCars
                .SelectMany(kvp => kvp.Value.Select(car => (trainCar: car.TrainCar(), job: kvp.Key)))
                .ToDictionary(p => p.trainCar, p => p.job.ID);
        }

        /// The least time between index rebuilds forced by a lookup that found
        /// nothing.
        private const float JobIndexRebuildSeconds = 5f;
        private static float lastJobIndexBuild = float.NegativeInfinity;

        public static Job? JobForId(string jobId)
        {
            if (jobForId.TryGetValue(jobId, out var job))
                return job;

            // A miss used to rebuild the whole index from JobsManager. That is
            // right when a job has just been created, and ruinous when a car
            // still carries the ID of a job that has finished: every car update
            // rebuilt it again, several times a second, and never found
            // anything. One rebuild per interval covers the first case without
            // paying for the second.
            if (Time.time - lastJobIndexBuild < JobIndexRebuildSeconds)
                return null;
            RebuildJobIndex();
            jobForId.TryGetValue(jobId, out job);
            return job;
        }

        /// Drop the job index when the world goes. It is rebuilt wholesale from
        /// the live registry, so letting go of it is always safe - and holding
        /// jobs from a world that has been unloaded is not, now that a miss no
        /// longer rebuilds unconditionally.
        ///
        /// The per-car index is deliberately left alone: it is filled as the
        /// game assigns cars to jobs, not from a sweep, so clearing it would
        /// lose entries nothing would put back.
        public static void Reset()
        {
            jobForId = new Dictionary<string, Job>();
            lastJobIndexBuild = float.NegativeInfinity;
        }

        private static void RebuildJobIndex()
        {
            lastJobIndexBuild = Time.time;
            var rebuilt = new Dictionary<string, Job>();
            foreach (var job in SingletonBehaviour<JobsManager>.Instance.jobToJobCars.Keys)
            {
                if (job != null && job.ID != null)
                    rebuilt[job.ID] = job;   // indexer, not ToDictionary: a
                                             // duplicate ID must not throw here
            }
            jobForId = rebuilt;
        }

        public static Dictionary<string, JObject> GetAllJobData()
        {
            static IEnumerable<JObject> PassengerJson(TaskData sequenceTask)
            {
                var sequence = sequenceTask.nestedTasks.Select(task => task.GetTaskData()).ToList();

                string startTrackId = sequence[0].destinationTrack.ID.FullDisplayID;

                for (int i = 1; i < sequence.Count; i++)
                {
                    var task = sequence[i];
                    bool isRuralTask = task.type == (TaskType)42;

                    bool isRuralUnload = isRuralTask && !((dynamic)task).isLoading;
                    if ((task.warehouseTaskType != WarehouseTaskType.Unloading) && !isRuralUnload)
                    {
                        // skip everything but unload tasks
                        continue;
                    }

                    string destTrackId;

                    if (isRuralTask)
                    {
                        destTrackId = ((dynamic)task).stationId;
                    }
                    else
                    {
                        destTrackId = task.destinationTrack.ID.FullDisplayID;
                    }

                    yield return new JObject()
                    {
                        { "startTrack", startTrackId },
                        { "destinationTrack", destTrackId },
                        { "cars", new JArray(task.cars.Select(car => car.ID)) }
                    };

                    startTrackId = destTrackId;
                }
            }
            static IEnumerable<TaskData> FlattenToTransport(TaskData data)
            {
                if (data.type == TaskType.Transport)
                {
                    yield return data;
                }
                else if (data.nestedTasks != null)
                {
                    foreach (var nested in data.nestedTasks)
                    {
                        foreach (var task in FlattenToTransport(nested.GetTaskData()))
                            yield return task;
                    }
                }
            }
            static IEnumerable<TaskData> FlattenMany(IEnumerable<TaskData> data) => data.SelectMany(FlattenToTransport);
            static JObject TaskToJson(TaskData data) => new JObject(
                new JProperty("startTrack", data.startTrack?.ID?.FullDisplayID),
                new JProperty("destinationTrack", data.destinationTrack?.ID?.FullDisplayID),
                new JProperty("cars", (data.cars ?? new List<Car>()).Select(car => car.ID))
            );
            static JArray RequiredLicenses(Job job) => JArray.FromObject(
                Enum.GetValues(typeof(JobLicenses))
                    .OfType<JobLicenses>()
                    .Where(v => (job.requiredLicenses & LicensesToExport & v) != JobLicenses.Basic)
                    .Select(v => Enum.GetName(typeof(JobLicenses), v))
            );
            static float TotalLength(TaskData task) => task.cars.Sum(car => car.length);
            static float TotalMass(TaskData task) => task.cars.Sum(car => car.carType.parentType.mass)
                + ((task.cargoTypePerCar == null)
                ? 0f
                : task.cars.Zip(task.cargoTypePerCar, (car, cargoType) => car.capacity * cargoType.ToV2().massPerUnit).Sum());

            static JObject JobToJson(Job job)
            {
                IEnumerable<JObject> taskJson;
                TaskData mainTask;

                if (job.jobType <= JobType.ComplexTransport)
                {
                    // normal job
                    var flattenedTasks = FlattenMany(job.tasks.Select(task => task.GetTaskData())).ToArray();
                    mainTask = job.jobType == JobType.ShuntingLoad ? flattenedTasks.Last() : flattenedTasks.First();

                    taskJson = flattenedTasks.Select(TaskToJson);
                }
                else
                {
                    // passenger
                    var sequenceTask = job.tasks[0].GetTaskData();
                    mainTask = sequenceTask.nestedTasks[0].GetTaskData();
                    taskJson = PassengerJson(sequenceTask);
                }

                return new JObject(
                    new JProperty("originYardId", job.chainData.chainOriginYardId),
                    new JProperty("destinationYardId", job.chainData.chainDestinationYardId),
                    new JProperty("tasks", taskJson),
                    new JProperty("requiredLicenses", RequiredLicenses(job)),
                    new JProperty("length", TotalLength(mainTask)),
                    new JProperty("mass", TotalMass(mainTask) / 1000),
                    new JProperty("basePayment", job.GetBasePaymentForTheJob()),
                    new JProperty("isActive", job.State == JobState.InProgress));
            }

            // The whole job list is being written, so this one is worth an
            // unconditional rebuild rather than the rate-limited one a lookup
            // gets.
            RebuildJobIndex();
            return jobForId.ToDictionary(
                kvp => kvp.Key,
                kvp => JobToJson(kvp.Value));
        }

        public static string GetAllJobDataJson()
        {
            return JsonConvert.SerializeObject(GetAllJobData());
        }

        public static class JobPatches
        {
            [HarmonyPatch(typeof(JobChainController), nameof(JobChainController.UpdateTrainCarPlatesOfCarsOnJob))]
            public static class UpdateTrainCarPlatesOfCarsOnJobPatch
            {
                public static void Postfix(JobChainController __instance, string jobId)
                {
                    foreach (Car car in __instance.carsForJobChain)
                    {
                        var trainCar = car.TrainCar();

                        if (jobId.Length == 0)
                            jobIdForCar.Remove(trainCar);
                        else
                            jobIdForCar[trainCar] = jobId;
                        Sessions.AddTag("jobs");
                    }
                }
            }
            public static void UpdateJobsFromPersistentJobs(Job job)
            {
                Main.DebugLog(() => "Persistent Jobs sent update for job " + job.ID);
                Sessions.AddTag("jobs");
            }
            [HarmonyPatch(typeof(Job))]
            public static class UpdateJobStatePatches
            {
                [HarmonyPostfix]
                [HarmonyPatch(nameof(Job.TakeJob))]
                public static void TakeJobPostfix(Job __instance, bool takenViaLoadGame)
                {
                    if (!takenViaLoadGame)
                    {
                        Sessions.AddTag("jobs");
                    }
                }
            }
        }
    }
}