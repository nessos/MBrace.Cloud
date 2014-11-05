﻿namespace Nessos.MBrace.SampleRuntime

    open System.IO
    open System.Diagnostics
    open System.Threading

    open Nessos.MBrace
    open Nessos.MBrace.Runtime
    open Nessos.MBrace.Runtime.Compiler
    open Nessos.MBrace.SampleRuntime.Tasks

    module internal Argument =
        let ofRuntime (runtime : RuntimeState) =
            let pickle = VagrantRegistry.Pickler.Pickle(runtime)
            System.Convert.ToBase64String pickle

        let toRuntime (args : string []) =
            let bytes = System.Convert.FromBase64String(args.[0])
            VagrantRegistry.Pickler.UnPickle<RuntimeState> bytes

    /// MBrace Sample runtime client instance.
    type MBraceRuntime private (state : RuntimeState, procs : Process []) =
        static let mutable exe = None
        let mutable procs = procs

        static let initWorkers (target : RuntimeState) (count : int) =
            if count < 1 then invalidArg "workerCount" "must be positive."
            let exe = MBraceRuntime.WorkerExecutable    
            let args = Argument.ofRuntime target
            let psi = new ProcessStartInfo(exe, args)
            psi.WorkingDirectory <- Path.GetDirectoryName exe
            psi.UseShellExecute <- true
            Array.init count (fun _ -> Process.Start psi)
        
        /// Asynchronously execute a workflow on the distributed runtime.
        member __.RunAsync(workflow : Cloud<'T>, ?cancellationToken : CancellationToken) = async {
            let computation = CloudCompiler.Compile workflow
            let! cts = state.ResourceFactory.RequestCancellationTokenSource()
            try
                cancellationToken |> Option.iter (fun ct -> ct.Register(fun () -> cts.Cancel()) |> ignore)
                let! resultCell = state.StartAsCell computation.Dependencies cts computation.Workflow
                let! result = resultCell.AwaitResult()
                return result.Value
            finally
                cts.Cancel ()
        }

        /// Execute a workflow on the distributed runtime as task.
        member __.RunAsTask(workflow : Cloud<'T>, ?cancellationToken : CancellationToken) =
            let asyncwf = __.RunAsync(workflow, ?cancellationToken = cancellationToken)
            Async.StartAsTask(asyncwf)

        /// Execute a workflow on the distributed runtime synchronously
        member __.Run(workflow : Cloud<'T>, ?cancellationToken : CancellationToken) =
            __.RunAsync(workflow, ?cancellationToken = cancellationToken) |> Async.RunSynchronously

        /// Violently kills all worker nodes in the runtime
        member __.KillAllWorkers () = lock procs (fun () -> for p in procs do try p.Kill() with _ -> () ; procs <- [||])
        /// Gets all worker processes in the runtime
        member __.Workers = procs
        /// Appens count of new worker processes to the runtime.
        member __.AppendWorkers (count : int) =
            let newProcs = initWorkers state count
            lock procs (fun () -> procs <- Array.append procs newProcs)

        /// Initialize a new local rutime instance with supplied worker count.
        static member InitLocal(workerCount : int) =
            let state = RuntimeState.InitLocal()
            let workers = initWorkers state workerCount
            new MBraceRuntime(state, workers)

        /// Gets or sets the worker executable location.
        static member WorkerExecutable
            with get () = match exe with None -> invalidOp "unset executable path." | Some e -> e
            and set path = 
                let path = Path.GetFullPath path
                if File.Exists path then exe <- Some path
                else raise <| FileNotFoundException(path)