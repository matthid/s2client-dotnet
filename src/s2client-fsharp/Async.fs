namespace Starcraft2

/// Extensions for async workflows.
[<AutoOpen>]
module AsyncExtensions = 
  open System
  open System.Threading.Tasks
  open System.Threading
  open System.Runtime.ExceptionServices
  
  // This uses a trick to get the underlying OperationCanceledException
  let inline getCancelledException (completedTask:Task) (waitWithAwaiter) =
      let fallback = new TaskCanceledException(completedTask) :> OperationCanceledException
      // sadly there is no other public api to retrieve it, but to call .GetAwaiter().GetResult().
      try waitWithAwaiter()
          // should not happen, but just in case...
          fallback
      with
      | :? OperationCanceledException as o -> o
      | other ->
          // shouldn't happen, but just in case...
          new TaskCanceledException(fallback.Message, other) :> OperationCanceledException
  type Microsoft.FSharp.Control.Async with 
    static member AwaitTaskWithoutAggregate (task:Task<'T>) : Async<'T> =
        Async.FromContinuations(fun (cont, econt, ccont) ->
            let continuation (completedTask : Task<_>) =
                if completedTask.IsCanceled then
                    let cancelledException =
                        getCancelledException completedTask (fun () -> completedTask.GetAwaiter().GetResult() |> ignore)
                    econt (cancelledException)
                elif completedTask.IsFaulted then
                    if completedTask.Exception.InnerExceptions.Count = 1 then
                        econt completedTask.Exception.InnerExceptions.[0]
                    else
                        econt completedTask.Exception
                else
                    cont completedTask.Result
            task.ContinueWith(Action<Task<'T>>(continuation)) |> ignore)
    static member AwaitTaskWithoutAggregate (task:Task) : Async<unit> =
        Async.FromContinuations(fun (cont, econt, ccont) ->
            let continuation (completedTask : Task) =
                if completedTask.IsCanceled then
                    let cancelledException =
                        getCancelledException completedTask (fun () -> completedTask.GetAwaiter().GetResult() |> ignore)
                    econt (cancelledException)
                elif completedTask.IsFaulted then
                    if completedTask.Exception.InnerExceptions.Count = 1 then
                        econt completedTask.Exception.InnerExceptions.[0]
                    else
                        econt completedTask.Exception
                else
                    cont ()
            task.ContinueWith(Action<Task>(continuation)) |> ignore)