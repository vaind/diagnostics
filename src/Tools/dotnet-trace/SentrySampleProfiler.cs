using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Stacks;
using Sentry;
using Sentry.Extensibility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Microsoft.Diagnostics.Tools.Trace
{
    /// A list of frame indexes.
    using SentryProfileStackTrace = HashableGrowableArray<int>;

    /// <summary>
    /// Processes TraceLog to compose a SentrySampleProfile.
    ///
    /// Based on https://github.com/microsoft/perfview/blob/d4c209ad680`2de03ff4c595714b2b7714da036f/src/TraceEvent/Computers/SampleProfilerThreadTimeComputer.cs
    /// </summary>
    internal class SentrySampleProfiler
    {
        /// <summary>
        /// If set we compute thread time using Tasks
        /// </summary>
        private bool UseTasks = true;

        ///// <summary>
        ///// Use start-stop activities as the grouping construct.
        ///// </summary>
        //private bool GroupByStartStopActivity = true;

        /// <summary>
        /// Reduce nested application insights requests by using related activity id.
        /// </summary>
        /// <value></value>
        private bool IgnoreApplicationInsightsRequestsWithRelatedActivityId { get; set; } = true;


        private readonly TraceLog _traceLog; 
        TraceLogEventSource _eventSource;

        /// <summary>
        /// Output profile being built.
        /// </summary>
        private readonly SentrySampleProfile _profile = new();

        // A sparse array that maps from StackSourceFrameIndex to an index in the output Profile.frames.
        private SparseScalarArray<int> _frameIndexes = new(-1, 1000);

        // A dictionary from a StackTrace sealed array to an index in the output Profile.stacks.
        private Dictionary<SentryProfileStackTrace, int> _stackIndexes = new(100);

        //private Tracing.StartStopActivityComputer _startStopActivities;    // Tracks start-stop activities so we can add them to the top above thread in the stack.

        // UNKNOWN_ASYNC support
        ///// <summary>
        ///// Used to create UNKNOWN frames for start-stop activities.   This is indexed by Tracing.StartStopActivityIndex.
        ///// and for each start-stop activity indicates when unknown time starts.   However if that activity still
        ///// has known activities associated with it then the number will be negative, and its value is the
        ///// ref-count of known activities (thus when it falls to 0, it we set it to the start of unknown time.
        ///// This is indexed by the TOP-MOST start-stop activity.
        ///// </summary>
        //private GrowableArray<double> _unknownTimeStartMsec;

        ///// <summary>
        ///// maps thread ID to the current TOP-MOST start-stop activity running on that thread.   Used to updated _unknownTimeStartMsec
        ///// to figure out when to put in UNKNOWN_ASYNC nodes.
        ///// </summary>
        //private Tracing.StartStopActivity[] _threadToStartStopActivity;

        ///// <summary>
        ///// Sadly, with AWAIT nodes might come into existence AFTER we would have normally identified
        ///// a region as having no thread/await working on it.  Thus you have to be able to 'undo' ASYNC_UNKONWN
        ///// nodes.   We solve this by remembering all of our ASYNC_UNKNOWN nodes on a list (basically provisional)
        ///// and only add them when the start-stop activity dies (when we know there can't be another AWAIT.
        ///// Note that we only care about TOP-MOST activities.
        ///// </summary>
        //private GrowableArray<List<StackSourceSample>> _startStopActivityToAsyncUnknownSamples;

        // End UNKNOWN_ASYNC support

        //private ThreadState[] _threadState;            // This maps thread (indexes) to what we know about the thread

        //private StackSourceSample _sample;                 // Reusable scratch space
        private MutableTraceEventStackSource _stackSource; // The output source we are generating.
        // private TraceEventStackSource _stackSource;

        //// These are boring caches of frame names which speed things up a bit.
        //private StackSourceFrameIndex _ExternalFrameIndex;
        //private StackSourceFrameIndex _cpuFrameIndex;
        private Tracing.ActivityComputer _activityComputer;                        // Used to compute stacks for Tasks

        public SentrySampleProfiler(TraceLog traceLog)
        {
            _traceLog = traceLog;
            _eventSource = _traceLog.Events.GetSource();
            _stackSource = new MutableTraceEventStackSource(_traceLog)
            {
                //_stackSource = new TraceEventStackSource(_eventLog.Events) {
                OnlyManagedCodeStacks = true // EventPipe currently only has managed code stacks.
            };
            //_sample = new StackSourceSample(_stackSource);
            //_ExternalFrameIndex = _stackSource.Interner.FrameIntern("UNMANAGED_CODE_TIME");
            //_cpuFrameIndex = _stackSource.Interner.FrameIntern("CPU_TIME");

            //if (GroupByStartStopActivity) {
            //    UseTasks = true;
            //}

            if (UseTasks)
            {
                _activityComputer = new Tracing.ActivityComputer(_eventSource, new SymbolReader(TextWriter.Null));
                _activityComputer.AwaitUnblocks += delegate (TraceActivity activity, Tracing.TraceEvent data)
                {
                    //var sample = _sample;
                    //sample.Metric = (float)(activity.StartTimeRelativeMSec - activity.CreationTimeRelativeMSec);
                    //sample.TimeRelativeMSec = activity.CreationTimeRelativeMSec;

                    //// The stack at the Unblock, is the stack at the time the task was created (when blocking started).
                    //sample.StackIndex = _activityComputer.GetCallStackForActivity(_stackSource, activity, GetTopFramesForActivityComputerCase(data, data.Thread(), true));

                    //StackSourceFrameIndex awaitFrame = _stackSource.Interner.FrameIntern("AWAIT_TIME");
                    //sample.StackIndex = _stackSource.Interner.CallStackIntern(awaitFrame, sample.StackIndex);

                    //_stackSource.AddSample(sample);

                    //if (_threadToStartStopActivity != null) {
                    //    UpdateStartStopActivityOnAwaitComplete(activity, data);
                    //}
                };

                // We can provide a bit of extra value (and it is useful for debugging) if we immediately log a CPU
                // sample when we schedule or start a task.  That we we get the very instant it starts.
                var tplProvider = new TplEtwProviderTraceEventParser(_eventSource);
                tplProvider.AwaitTaskContinuationScheduledSend += OnSampledProfile;
                tplProvider.TaskScheduledSend += OnSampledProfile;
                tplProvider.TaskExecuteStart += OnSampledProfile;
                tplProvider.TaskWaitSend += OnSampledProfile;
                tplProvider.TaskWaitStop += OnTaskUnblock;  // Log the activity stack even if you don't have a stack.
            }

            //if (GroupByStartStopActivity) {
            //    _startStopActivities = new Tracing.StartStopActivityComputer(_eventSource, _activityComputer, IgnoreApplicationInsightsRequestsWithRelatedActivityId);

            //    // Maps thread Indexes to the start-stop activity that they are executing.
            //    _threadToStartStopActivity = new Tracing.StartStopActivity[_eventLog.Threads.Count];

            //    /*********  Start Unknown Async State machine for StartStop activities ******/
            //    // The delegates below along with the AddUnkownAsyncDurationIfNeeded have one purpose:
            //    // To inject UNKNOWN_ASYNC stacks when there is an active start-stop activity that is
            //    // 'missing' time.   It has the effect of ensuring that Start-Stop tasks always have
            //    // a metric that is not unrealistically small.
            //    _activityComputer.Start += delegate (TraceActivity activity, Tracing.TraceEvent data) {
            //        Tracing.StartStopActivity newStartStopActivityForThread = _startStopActivities.GetCurrentStartStopActivity(activity.Thread, data);
            //        UpdateThreadToWorkOnStartStopActivity(activity.Thread, newStartStopActivityForThread, data);
            //    };

            //    _activityComputer.AfterStop += delegate (TraceActivity activity, Tracing.TraceEvent data, TraceThread thread) {
            //        Tracing.StartStopActivity newStartStopActivityForThread = _startStopActivities.GetCurrentStartStopActivity(thread, data);
            //        UpdateThreadToWorkOnStartStopActivity(thread, newStartStopActivityForThread, data);
            //    };

            //    _startStopActivities.Start += delegate (Tracing.StartStopActivity startStopActivity, Tracing.TraceEvent data) {
            //        // We only care about the top-most activities since unknown async time is defined as time
            //        // where a top  most activity is running but no thread (or await time) is associated with it
            //        // fast out otherwise (we just ensure that we mark the thread as doing this activity)
            //        if (startStopActivity.Creator != null) {
            //            UpdateThreadToWorkOnStartStopActivity(data.Thread(), startStopActivity, data);
            //            return;
            //        }

            //        // Then we have a refcount of exactly one
            //        Debug.Assert(_unknownTimeStartMsec.Get((int)startStopActivity.Index) >= 0);    // There was nothing running before.

            //        _unknownTimeStartMsec.Set((int)startStopActivity.Index, -1);       // Set it so just we are running.
            //        _threadToStartStopActivity[(int)data.Thread().ThreadIndex] = startStopActivity;
            //    };

            //    _startStopActivities.Stop += delegate (Tracing.StartStopActivity startStopActivity, Tracing.TraceEvent data) {
            //        // We only care about the top-most activities since unknown async time is defined as time
            //        // where a top  most activity is running but no thread (or await time) is associated with it
            //        // fast out otherwise
            //        if (startStopActivity.Creator != null) {
            //            return;
            //        }

            //        double unknownStartTime = _unknownTimeStartMsec.Get((int)startStopActivity.Index);
            //        if (0 < unknownStartTime) {
            //            AddUnkownAsyncDurationIfNeeded(startStopActivity, unknownStartTime, data);
            //        }

            //        // Actually emit all the async unknown events.
            //        List<StackSourceSample> samples = _startStopActivityToAsyncUnknownSamples.Get((int)startStopActivity.Index);
            //        if (samples != null) {
            //            foreach (var sample in samples) {
            //                _stackSource.AddSample(sample);  // Adding Unknown ASync
            //            }

            //            _startStopActivityToAsyncUnknownSamples.Set((int)startStopActivity.Index, null);
            //        }

            //        _unknownTimeStartMsec.Set((int)startStopActivity.Index, 0);
            //        Debug.Assert(_threadToStartStopActivity[(int)data.Thread().ThreadIndex] == startStopActivity ||
            //            _threadToStartStopActivity[(int)data.Thread().ThreadIndex] == null);
            //        _threadToStartStopActivity[(int)data.Thread().ThreadIndex] = null;
            //    };
            //}

            _eventSource.Clr.GCAllocationTick += OnSampledProfile;
            _eventSource.Clr.GCSampledObjectAllocation += OnSampledProfile;

            var sampleEventParser = new SampleProfilerTraceEventParser(_eventSource);
            sampleEventParser.ThreadSample += OnSampledProfile;
        }

        public SentrySampleProfile Process() {

            _eventSource.Process();
            return _profile;
        }

        //private void UpdateStartStopActivityOnAwaitComplete(TraceActivity activity, Tracing.TraceEvent data) {
        //    // If we are createing 'UNKNOWN_ASYNC nodes, make sure that AWAIT_TIME does not overlap with UNKNOWN_ASYNC time

        //    var startStopActivity = _startStopActivities.GetStartStopActivityForActivity(activity);
        //    if (startStopActivity == null) {
        //        return;
        //    }

        //    while (startStopActivity.Creator != null) {
        //        startStopActivity = startStopActivity.Creator;
        //    }

        //    // If the await finishes before the ASYNC_UNKNOWN, simply adust the time.
        //    if (0 <= _unknownTimeStartMsec.Get((int)startStopActivity.Index)) {
        //        _unknownTimeStartMsec.Set((int)startStopActivity.Index, data.TimeStampRelativeMSec);
        //    }

        //    // It is possible that the ASYNC_UNKOWN has already completed.  In that case, remove overlapping ones
        //    List<StackSourceSample> async_unknownSamples = _startStopActivityToAsyncUnknownSamples.Get((int)startStopActivity.Index);
        //    if (async_unknownSamples != null) {
        //        int removeStart = async_unknownSamples.Count;
        //        while (0 < removeStart) {
        //            int probe = removeStart - 1;
        //            var sample = async_unknownSamples[probe];
        //            if (activity.CreationTimeRelativeMSec <= sample.TimeRelativeMSec + sample.Metric) // There is overlap
        //            {
        //                removeStart = probe;
        //            }
        //            else {
        //                break;
        //            }
        //        }
        //        int removeCount = async_unknownSamples.Count - removeStart;
        //        if (removeCount > 0) {
        //            async_unknownSamples.RemoveRange(removeStart, removeCount);
        //        }
        //    }
        //}

        ///// <summary>
        ///// Updates it so that 'thread' is now working on newStartStop, which can be null which means that it is not working on any
        ///// start-stop task.
        ///// </summary>
        //private void UpdateThreadToWorkOnStartStopActivity(TraceThread thread, Tracing.StartStopActivity newStartStop, Tracing.TraceEvent data) {
        //    // Make the new-start stop activity be the top most one.   This is all we need and is more robust in the case
        //    // of unusual state transitions (e.g. lost events non-nested start-stops ...).  Ref-counting is very fragile
        //    // after all...
        //    if (newStartStop != null) {
        //        while (newStartStop.Creator != null) {
        //            newStartStop = newStartStop.Creator;
        //        }
        //    }

        //    Tracing.StartStopActivity oldStartStop = _threadToStartStopActivity[(int)thread.ThreadIndex];
        //    Debug.Assert(oldStartStop == null || oldStartStop.Creator == null);
        //    if (oldStartStop == newStartStop)       // No change, nothing to do, quick exit.
        //    {
        //        return;
        //    }

        //    // Decrement the start-stop which lost its thread.
        //    if (oldStartStop != null) {
        //        double unknownStartTimeMSec = _unknownTimeStartMsec.Get((int)oldStartStop.Index);
        //        Debug.Assert(unknownStartTimeMSec < 0);
        //        if (unknownStartTimeMSec < 0) {
        //            unknownStartTimeMSec++;     //We represent the ref count as a negative number, here we are decrementing the ref count
        //            if (unknownStartTimeMSec == 0) {
        //                unknownStartTimeMSec = data.TimeStampRelativeMSec;      // Remember when we dropped to zero.
        //            }

        //            _unknownTimeStartMsec.Set((int)oldStartStop.Index, unknownStartTimeMSec);
        //        }
        //    }
        //    _threadToStartStopActivity[(int)thread.ThreadIndex] = newStartStop;

        //    // Increment refcount on the new startStop activity
        //    if (newStartStop != null) {
        //        double unknownStartTimeMSec = _unknownTimeStartMsec.Get((int)newStartStop.Index);
        //        // If we were off before (a positive number) then log the unknown time.
        //        if (0 < unknownStartTimeMSec) {
        //            AddUnkownAsyncDurationIfNeeded(newStartStop, unknownStartTimeMSec, data);
        //            unknownStartTimeMSec = 0;
        //        }
        //        --unknownStartTimeMSec;     //We represent the ref count as a negative number, here we are incrementing the ref count
        //        _unknownTimeStartMsec.Set((int)newStartStop.Index, unknownStartTimeMSec);
        //    }
        //}

        //private void AddUnkownAsyncDurationIfNeeded(Tracing.StartStopActivity startStopActivity, double unknownStartTimeMSec, Tracing.TraceEvent data) {
        //    Debug.Assert(0 < unknownStartTimeMSec);
        //    Debug.Assert(unknownStartTimeMSec <= data.TimeStampRelativeMSec);

        //    if (startStopActivity.IsStopped) {
        //        return;
        //    }

        //    // We dont bother with times that are too small, we consider 1msec the threshold
        //    double delta = data.TimeStampRelativeMSec - unknownStartTimeMSec;
        //    if (delta < 1) {
        //        return;
        //    }

        //    // Add a sample with the amount of unknown duration.
        //    var sample = new StackSourceSample(_stackSource);
        //    sample.Metric = (float)delta;
        //    sample.TimeRelativeMSec = unknownStartTimeMSec;

        //    StackSourceCallStackIndex stackIndex = _startStopActivities.GetStartStopActivityStack(_stackSource, startStopActivity, data.Process());
        //    StackSourceFrameIndex unknownAsyncFrame = _stackSource.Interner.FrameIntern("UNKNOWN_ASYNC");
        //    stackIndex = _stackSource.Interner.CallStackIntern(unknownAsyncFrame, stackIndex);
        //    sample.StackIndex = stackIndex;

        //    // We can't add the samples right now because AWAIT nodes might overlap and we have to take these back.
        //    // The add the to this list so that they can be trimmed at that time if needed.

        //    List<StackSourceSample> list = _startStopActivityToAsyncUnknownSamples.Get((int)startStopActivity.Index);
        //    if (list == null) {
        //        list = new List<StackSourceSample>();
        //        _startStopActivityToAsyncUnknownSamples.Set((int)startStopActivity.Index, list);
        //    }
        //    list.Add(sample);
        //}

        private void AddSample(TraceThread thread, StackSourceCallStackIndex callstackIndex, double timestampMs)
        {
            if (thread.ThreadIndex == ThreadIndex.Invalid || callstackIndex == StackSourceCallStackIndex.Invalid)
            {
                return;
            }

            var stackIndex = AddStackTrace(callstackIndex);
            if (stackIndex < 0)
            {
                return;
            }

            var threadIndex = AddThread(thread);
            if (threadIndex < 0) {
                return;
            }

            _profile.samples.Add(new()
            {
                Timestamp = (ulong)(timestampMs * 1_000_000),
                StackId = stackIndex,
                ThreadId = threadIndex
            });
        }

        /// <summary>
        /// Adds stack trace and frames, if missing.
        /// </summary>
        /// <param name="callstackIndex"></param>
        /// <returns>The index into the Profile's stacks list</returns>
        private int AddStackTrace(StackSourceCallStackIndex callstackIndex)
        {
            SentryProfileStackTrace stackTrace = new(5);
            StackSourceFrameIndex tlFrameIndex;
            while (callstackIndex != StackSourceCallStackIndex.Invalid)
            {
                tlFrameIndex = _stackSource.GetFrameIndex(callstackIndex);

                if (tlFrameIndex == StackSourceFrameIndex.Invalid)
                {
                    break;
                }

                stackTrace.Add(AddStackFrame(tlFrameIndex));
                callstackIndex = _stackSource.GetCallerIndex(callstackIndex);
            }

            int result = -1;
            if (stackTrace.Count > 0)
            {
                stackTrace.Seal(5);
                if (!_stackIndexes.TryGetValue(stackTrace, out result))
                {
                    _profile.stacks.Add(stackTrace);
                    _stackIndexes[stackTrace] = _profile.stacks.Count - 1;
                }
            }

            return result;
        }

        /// <summary>
        /// Check if the frame is already stored in the output Profile, or adds it.
        /// </summary>
        /// <param name="frameIndex"></param>
        /// <returns>The index to the output Profile frames array.</returns>
        private int AddStackFrame(StackSourceFrameIndex frameIndex)
        {
            var key = (int)frameIndex;

            if (!_frameIndexes.ContainsKey(key))
            {
                _profile.frames.Add(CreateStackFrame(frameIndex));
                _frameIndexes[key] = _profile.frames.Count - 1;
            }

            return _frameIndexes[key];
        }

        /// <summary>
        /// Check if the thread is already stored in the output Profile, or adds it.
        /// </summary>
        /// <param name="frameIndex"></param>
        /// <returns>The index to the output Profile frames array.</returns>
        private int AddThread(TraceThread thread) {
            var key = (int)thread.ThreadIndex;

            if (!_profile.threads.ContainsKey(key)) {
                _profile.threads[key] = new() {
                    Id = key,
                    // TODO it should be possible to get the actual name of the thread somehow - speedscope output has it among frames, e.g. "Thread (30396) (.NET ThreadPool)"
                    Name = thread.ThreadInfo
                };
            }

            return key;
        }

        // TODO align this with Sentry's StackTraceFactory
        private SentryStackFrame CreateStackFrame(StackSourceFrameIndex frameIndex) {
            var frame = new SentryStackFrame();

            CodeAddressIndex codeAddressIndex = _stackSource.GetFrameCodeAddress(frameIndex);
            if (codeAddressIndex != CodeAddressIndex.Invalid) {
                TraceMethod method = _traceLog.CodeAddresses.Methods[_traceLog.CodeAddresses.MethodIndex(codeAddressIndex)];
                if (method is not null) {
                    frame.Function = method.FullMethodName;

                    TraceModuleFile moduleFile = method.MethodModuleFile;
                    if (moduleFile is not null) {
                        frame.Module = moduleFile.Name;
                    }
                }

                var ilOffset = _traceLog.CodeAddresses.ILOffset(codeAddressIndex);
                if (ilOffset >= 0) {
                    frame.InstructionAddress = $"0x{ilOffset:x}";
                }

                // TODO check if this is useful
                // Displays the optimization tier of each code version executed for the method.
                //if (ShowOptimizationTiers) {
                //    text = TraceMethod.PrefixOptimizationTier(text, _traceLog.CodeAddresses.OptimizationTier(codeAddress));
                //}
            }

            return frame;
        }

        private void OnSampledProfile(Tracing.TraceEvent data)
        {
            TraceThread thread = data.Thread();
            if (thread != null)
            {
                StackSourceCallStackIndex stackFrameIndex = GetCallStack(data, thread);

                //bool onCPU = (data is ClrThreadSampleTraceData) ? ((ClrThreadSampleTraceData)data).Type == ClrThreadSampleType.Managed : true;
                //_threadState[(int)thread.ThreadIndex].LogThreadStack(data.TimeStampRelativeMSec, stackIndex, thread, this, onCPU);
                AddSample(thread, stackFrameIndex, data.TimeStampRelativeMSec);
            }
            else
            {
                Debug.WriteLine("Warning, no thread at " + data.TimeStampRelativeMSec.ToString("f3"));
            }
        }

        // THis is for the TaskWaitEnd.  We want to have a stack event if 'data' does not have one, we lose the fact that
        // ANYTHING happened on this thread.   Thus we log the stack of the activity so that data does not need a stack.
        private void OnTaskUnblock(Tracing.TraceEvent data)
        {
            if (_activityComputer == null)
            {
                return;
            }

            TraceThread thread = data.Thread();
            if (thread != null)
            {
                TraceActivity activity = _activityComputer.GetCurrentActivity(thread);

                //StackSourceCallStackIndex stackIndex = _activityComputer.GetCallStackForActivity(_stackSource, activity, GetTopFramesForActivityComputerCase(data, data.Thread()));
                StackSourceCallStackIndex stackFrameIndex = _activityComputer.GetCallStackForActivity(_stackSource, activity);
                //_threadState[(int)thread.ThreadIndex].LogThreadStack(data.TimeStampRelativeMSec, stackIndex, thread, this, onCPU: true);
                AddSample(thread, stackFrameIndex, data.TimeStampRelativeMSec);
            }
            else
            {
                Debug.WriteLine("Warning, no thread at " + data.TimeStampRelativeMSec.ToString("f3"));
            }
        }

        /// <summary>
        /// Get the call stack for 'data'  Note that you thread must be data.Thread().   We pass it just to save the lookup.
        /// </summary>
        private StackSourceCallStackIndex GetCallStack(Tracing.TraceEvent data, TraceThread thread)
        {
            Debug.Assert(data.Thread() == thread);

            //return _activityComputer.GetCallStack(_stackSource, data, GetTopFramesForActivityComputerCase(data, thread));
            return _activityComputer.GetCallStack(_stackSource, data);
        }

        ///// <summary>
        ///// Returns a function that figures out the top (closest to stack root) frames for an event.  Often
        ///// this returns null which means 'use the normal thread-process frames'.
        ///// Normally this stack is for the current time, but if 'getAtCreationTime' is true, it will compute the
        ///// stack at the time that the current activity was CREATED rather than the current time.  This works
        ///// better for await time.
        ///// </summary>
        //private Func<TraceThread, StackSourceCallStackIndex> GetTopFramesForActivityComputerCase(Tracing.TraceEvent data, TraceThread thread, bool getAtCreationTime = false) {
        //    Debug.Assert(_activityComputer != null);
        //    return (topThread => _startStopActivities.GetCurrentStartStopActivityStack(_stackSource, thread, topThread, getAtCreationTime));
        //}

        ///// <summary>
        ///// Represents all the information that we need to track for each thread.
        ///// </summary>
        //private struct ThreadState {
        //    public void LogThreadStack(double timeRelativeMSec, StackSourceCallStackIndex stackIndex, TraceThread thread, SentrySampleProfiler computer, bool onCPU) {
        //        if (onCPU) {
        //            if (ThreadUninitialized) // First event is onCPU
        //            {
        //                AddCPUSample(timeRelativeMSec, thread, computer);
        //                LastBlockStackRelativeMSec = -1; // make ThreadRunning true
        //            }
        //            else if (ThreadRunning) // continue running
        //            {
        //                AddCPUSample(timeRelativeMSec, thread, computer);
        //            }
        //            else if (ThreadBlocked) // unblocked
        //            {
        //                AddBlockTimeSample(timeRelativeMSec, thread, computer);
        //                LastBlockStackRelativeMSec = -timeRelativeMSec;
        //            }

        //            LastCPUStackRelativeMSec = timeRelativeMSec;
        //            LastCPUCallStack = stackIndex;
        //        }
        //        else {
        //            if (ThreadBlocked || ThreadUninitialized) // continue blocking or assume we started blocked
        //            {
        //                AddBlockTimeSample(timeRelativeMSec, thread, computer);
        //            }
        //            else if (ThreadRunning) // blocked
        //            {
        //                AddCPUSample(timeRelativeMSec, thread, computer);
        //            }

        //            LastBlockStackRelativeMSec = timeRelativeMSec;
        //            LastBlockCallStack = stackIndex;
        //        }
        //    }

        //    public void AddCPUSample(double timeRelativeMSec, TraceThread thread, SentrySampleProfiler computer) {
        //        // Log the last sample if it was present
        //        if (LastCPUStackRelativeMSec > 0) {
        //            var sample = computer._sample;
        //            sample.Metric = (float)(timeRelativeMSec - LastCPUStackRelativeMSec);
        //            sample.TimeRelativeMSec = LastCPUStackRelativeMSec;

        //            var nodeIndex = computer._cpuFrameIndex;
        //            sample.StackIndex = LastCPUCallStack;

        //            sample.StackIndex = computer._stackSource.Interner.CallStackIntern(nodeIndex, sample.StackIndex);
        //            computer._stackSource.AddSample(sample); // CPU
        //        }
        //    }

        //    public void AddBlockTimeSample(double timeRelativeMSec, TraceThread thread, SentrySampleProfiler computer) {
        //        // Log the last sample if it was present
        //        if (LastBlockStackRelativeMSec > 0) {
        //            var sample = computer._sample;
        //            sample.Metric = (float)(timeRelativeMSec - LastBlockStackRelativeMSec);
        //            sample.TimeRelativeMSec = LastBlockStackRelativeMSec;

        //            var nodeIndex = computer._ExternalFrameIndex;       // BLOCKED_TIME
        //            sample.StackIndex = LastBlockCallStack;

        //            sample.StackIndex = computer._stackSource.Interner.CallStackIntern(nodeIndex, sample.StackIndex);
        //            computer._stackSource.AddSample(sample);
        //        }
        //    }

        //    public bool ThreadDead { get { return double.IsNegativeInfinity(LastBlockStackRelativeMSec); } }
        //    public bool ThreadRunning { get { return LastBlockStackRelativeMSec < 0 && !ThreadDead; } }
        //    public bool ThreadBlocked { get { return 0 < LastBlockStackRelativeMSec; } }
        //    public bool ThreadUninitialized { get { return LastBlockStackRelativeMSec == 0; } }

        //    /* State */
        //    internal double LastBlockStackRelativeMSec;        // Negative means not blocked, NegativeInfinity means dead.  0 means uninitialized.
        //    internal StackSourceCallStackIndex LastBlockCallStack;

        //    internal double LastCPUStackRelativeMSec;
        //    private StackSourceCallStackIndex LastCPUCallStack;
        //}
    }
    
    /// <summary>
    /// A GrowableArray that can be used as a key in a Dictionary.
    /// Note: it must be Seal()-ed before used as a key and can't be changed afterwards.
    /// </summary>
    internal sealed class HashableGrowableArray<T> : IEquatable<HashableGrowableArray<T>>
    {
        private GrowableArray<T> _items;
        private int _hashCode = 0;
        private bool _sealed = false;

        public HashableGrowableArray()
        {
            _items = new GrowableArray<T>();
        }

        public HashableGrowableArray(int capacity)
        {
            _items = new GrowableArray<T>(capacity);
        }

        public T this[int index]
        {
            get
            {
                return _items[index];
            }
            set
            {
                Debug.Assert(!_sealed);
                _items[index] = value;
            }
        }

        public int Count => _items.Count;

        //
        // Summary:
        //     Returns the underlying array. Should not be used most of the time!
        public GrowableArray<T> UnderlyingArray => _items;

        /// <summary>
        /// Seal this array so that it cannot be changed anymore and can be hashed.
        /// </summary>
        /// <param name="maxWaste">
        /// If more or equal to zero, trims the size of the array so that no more than the given number of slots are wasted.
        /// </param>
        public void Seal(int maxWaste = -1)
        {
            Debug.Assert(!_sealed);
            _sealed = true;
            if (maxWaste >= 0)
            {
                _items.Trim(maxWaste);
            }
            foreach (var item in _items)
            {
                _hashCode ^= item.GetHashCode();
            }
        }

        public void Add(T item)
        {
            Debug.Assert(!_sealed);
            _items.Add(item);
        }

        public override int GetHashCode()
        {
            Debug.Assert(_sealed);
            return _hashCode;
        }

        public bool Equals(HashableGrowableArray<T> rhs)
        {
            Debug.Assert(_sealed);
            if (ReferenceEquals(this, rhs))
                return true;
            if (ReferenceEquals(rhs, null))
                return false;
            if (rhs._hashCode != _hashCode)
                return false;
            var ic = _items.Count;
            if (ic != rhs._items.Count)
                return false;
            for (var i = 0; i < ic; ++i)
                if (!Equals(_items[i], rhs._items[i]))
                    return false;
            return true;
        }

        public override bool Equals(object obj)
        {
            Debug.Assert(_sealed);
            return Equals(obj as HashableGrowableArray<T>);
        }
    }

    /// <summary>
    /// Sparse array for scalars (value types). You must provide the uninitialized value that will be used for new unused elements.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal sealed class SparseScalarArray<T> where T : IEquatable<T> {
        private GrowableArray<T> _items;
        private T _uninitializedValue;

        public SparseScalarArray(T uninitializedValue) {
            _items = new GrowableArray<T>();
            _uninitializedValue = uninitializedValue;
        }

        public SparseScalarArray(T uninitializedValue, int capacity) {
            _items = new GrowableArray<T>(capacity);
            _uninitializedValue = uninitializedValue;
        }

        public T this[int index] {
            get {
                return _items[index];
            }
            set {
                // Increase the capacity of the sparse array so that the key can fit.
                while (_items.Count <= index) {
                    _items.Add(_uninitializedValue);
                }
                _items[index] = value;
            }
        }

        public bool ContainsKey(int key) {
            return key > 0 && key < _items.Count && !_uninitializedValue.Equals(_items[key]);
        }
    }

    /// <summary>
    /// Sparse array for objects. Null value is considered a missing item.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal sealed class SparseObjectArray<T> where T : class {
        private GrowableArray<T> _items;

        public SparseObjectArray() {
            _items = new GrowableArray<T>();
        }

        public SparseObjectArray(int capacity) {
            _items = new GrowableArray<T>(capacity);
        }

        public T this[int index] {
            get {
                return _items[index];
            }
            set {
                // Increase the capacity of the sparse array so that the key can fit.
                while (_items.Count <= index) {
                    _items.Add(null);
                }
                _items[index] = value;
            }
        }

        public bool ContainsKey(int key) {
            return key > 0 && key < _items.Count && _items[key] is not null;
        }

        /// <summary>
        /// Executes 'func(key, value)' for each element present.
        /// </summary>
        public void Foreach(Action<int, T> func) {
            for (int i = 0; i < _items.Count; i++) {
                if (_items[i] is not null) {
                    func(i, _items[i]);
                }
            }
        }

    }

    internal class SentrySampleProfile : IJsonSerializable {
        public GrowableArray<Sample> samples = new(10000);
        public GrowableArray<SentryStackFrame> frames = new(100);
        public GrowableArray<SentryProfileStackTrace> stacks = new(100);
        public SparseObjectArray<SentryThread> threads = new(10); 

        public void WriteTo(Utf8JsonWriter writer, IDiagnosticLogger logger) {
            writer.WriteStartObject();

            writer.WritePropertyName("thread_metadata");
            writer.WriteStartObject();
            threads.Foreach((k, v) => {
                writer.WritePropertyName(k.ToString());
                writer.WriteDynamicValue(v, logger);
            });
            writer.WriteEndObject();

            writer.WritePropertyName("stacks");
            writer.WriteStartArray();
            foreach (var stack in stacks) {
                writer.WriteGrowableArrayValue<int>(stack.UnderlyingArray, logger);
            }
            writer.WriteEndArray();
            writer.WriteGrowableArray<SentryStackFrame>("frames", frames, logger);

            writer.WriteGrowableArray<Sample>("samples", samples, logger);

            writer.WriteEndObject();
        }

        public class Sample : IJsonSerializable {
            /// <summary>
            /// Timestamp in nanoseconds relative to the profile start.
            /// </summary>
            public ulong Timestamp;

            public int ThreadId;
            public int StackId;

            public void WriteTo(Utf8JsonWriter writer, IDiagnosticLogger logger) {
                writer.WriteStartObject();

                writer.WriteNumber("elapsed_since_start_ns", Timestamp);
                writer.WriteNumber("thread_id", ThreadId);
                writer.WriteNumber("stack_id", StackId);

                writer.WriteEndObject();
            }
        }
    }
    internal static class JsonExtensions {

        public static void WriteGrowableArray<T>(
            this Utf8JsonWriter writer,
            string propertyName,
            GrowableArray<T>? arr,
            IDiagnosticLogger logger) {
            writer.WritePropertyName(propertyName);
            writer.WriteGrowableArrayValue(arr, logger);
        }

        public static void WriteGrowableArrayValue<T>(
            this Utf8JsonWriter writer,
            GrowableArray<T>? arr,
            IDiagnosticLogger logger) {
            if (arr is not null) {
                writer.WriteStartArray();

                foreach (var i in arr) {
                    writer.WriteDynamicValue(i, logger);
                }

                writer.WriteEndArray();
            }
            else {
                writer.WriteNullValue();
            }
        }

        // TODO remove - the rest is just a copy from src\Sentry\Internal\Extensions\JsonExtensions.cs


        public static void WriteSerializableValue(
            this Utf8JsonWriter writer,
            IJsonSerializable value,
            IDiagnosticLogger logger) {
            value.WriteTo(writer, logger);
        }

        public static void WriteSerializable(
            this Utf8JsonWriter writer,
            string propertyName,
            IJsonSerializable value,
            IDiagnosticLogger logger) {
            writer.WritePropertyName(propertyName);
            writer.WriteSerializableValue(value, logger);
        }

        public static void WriteArray(
            this Utf8JsonWriter writer,
            string propertyName,
            IEnumerable<object> arr,
            IDiagnosticLogger logger) {
            writer.WritePropertyName(propertyName);
            writer.WriteArrayValue(arr, logger);
        }

        public static void WriteArrayValue(
            this Utf8JsonWriter writer,
            IEnumerable<object> arr,
            IDiagnosticLogger logger) {
            if (arr is not null) {
                writer.WriteStartArray();

                foreach (var i in arr) {
                    writer.WriteDynamicValue(i, logger);
                }

                writer.WriteEndArray();
            }
            else {
                writer.WriteNullValue();
            }
        }

        public static void WriteDynamicValue(
            this Utf8JsonWriter writer,
            object value,
            IDiagnosticLogger logger) {
            if (value is null) {
                writer.WriteNullValue();
            }
            else if (value is IJsonSerializable serializable) {
                writer.WriteSerializableValue(serializable, logger);
            }
            //else if (value is IEnumerable<KeyValuePair<string, string?>> sdic) {
            //    writer.WriteStringDictionaryValue(sdic);
            //}
            //else if (value is IEnumerable<KeyValuePair<string, object?>> dic) {
            //    writer.WriteDictionaryValue(dic, logger);
            //}
            else if (value is string str) {
                writer.WriteStringValue(str);
            }
            else if (value is bool b) {
                writer.WriteBooleanValue(b);
            }
            else if (value is int i) {
                writer.WriteNumberValue(i);
            }
            else if (value is long l) {
                writer.WriteNumberValue(l);
            }
            else if (value is double d) {
                writer.WriteNumberValue(d);
            }
            else if (value is DateTime dt) {
                writer.WriteStringValue(dt);
            }
            else if (value is DateTimeOffset dto) {
                writer.WriteStringValue(dto);
            }
            else if (value is IFormattable formattable) {
                writer.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
            }
            else {
                JsonSerializer.Serialize(writer, value);
            }
        }

    }
}
