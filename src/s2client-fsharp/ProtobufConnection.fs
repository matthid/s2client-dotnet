namespace Starcraft2

open SC2APIProtocol
open System.Net.WebSockets

type Agent<'T> = MailboxProcessor<'T>


/// Exception for invalid expression types
[<System.Serializable>]
type ClientDisconnectedException =
    inherit System.Exception
    new (msg:string, inner:exn) = {
      inherit System.Exception(msg, inner) }
    new (info:System.Runtime.Serialization.SerializationInfo, context:System.Runtime.Serialization.StreamingContext) = {
      inherit System.Exception(info, context) }


/// Exception for invalid expression types
[<System.Serializable>]
type ResponseErrorException =
    static member FormatError (msgs:string seq) =
        System.String.Join(", ", msgs)
    val private errors : string list

    inherit System.Exception
    new (msg:string, inner:exn) = {
      inherit System.Exception(msg, inner)
      errors = [msg] }
    new (errors:string seq) = {
      inherit System.Exception(ResponseErrorException.FormatError(errors), null)
      errors = errors |> Seq.toList }
    new (info:System.Runtime.Serialization.SerializationInfo, context:System.Runtime.Serialization.StreamingContext) = {
      inherit System.Exception(info, context)
      errors = []
    }
    member x.Errors with get () = x.errors

/// Exception for invalid expression types
[<System.Serializable>]
type TypedResponseErrorException<'T when 'T : enum<int>> =
    static member FormatError (error:'T, detail:string) =
        let name = System.Enum.GetName(typeof<'T>, error)
        sprintf "%s - %s (%d): %s" (typeof<'T>.Name) name (error :> obj :?> int) detail
    val private error : 'T
    val private detail : string

    inherit ResponseErrorException
    new (msg:string, inner:exn) = {
      inherit ResponseErrorException(msg, inner)
      error = Unchecked.defaultof<'T>
      detail = "" }
    new (error:'T, detail : string) = {
      inherit ResponseErrorException(TypedResponseErrorException.FormatError(error, detail), null)
      error = error
      detail = detail }
    new (info:System.Runtime.Serialization.SerializationInfo, context:System.Runtime.Serialization.StreamingContext) = {
      inherit ResponseErrorException(info, context)
      error = Unchecked.defaultof<'T>
      detail = "" }
    member x.Error with get () = x.error
    member x.Detail with get () = x.detail

type PlayerId = uint32

// Handle connection via protobuf/websockets
module ProtbufConnection =
    type private ClientResponse<'T> =
        | Success of 'T
        | Error of System.Runtime.ExceptionServices.ExceptionDispatchInfo

    type private ClientRequest =
        | SendRequest of SC2APIProtocol.Request * AsyncReplyChannel<ClientResponse<SC2APIProtocol.Response>>
        | Disconnect of bool * AsyncReplyChannel<System.Runtime.ExceptionServices.ExceptionDispatchInfo option>

    type Sc2Connection =
        private { Client : Agent<ClientRequest>; _Address : string; _Port : int; _Timeout : System.TimeSpan }
        interface System.IDisposable with
            member x.Dispose () =
                x.Client.PostAndAsyncReply(fun reply -> Disconnect (false, reply))
                |> Async.RunSynchronously
                |> ignore
        member x.Disconnect (quitInstance: bool) =
            x.Client.PostAndAsyncReply(fun reply -> Disconnect(quitInstance, reply))
            |> Async.RunSynchronously
            |> ignore

        member x.Address = x._Address
        member x.Port = x._Port
        member x.Timeout = x._Timeout

    let private sendRequest (cl:Sc2Connection) request = async {
        let! response = cl.Client.PostAndAsyncReply(fun reply -> SendRequest (request, reply))
        match response with
        | Success res -> return res
        | Error dispatch -> dispatch.Throw(); return failwithf "Should not happen." }

    let private checkNullAndWarnings (response:Response) field =
        if isNull field then
            if isNull response.Error then
                failwithf "Unexpected result and no error information!"
            raise <| ResponseErrorException response.Error
        else
            if not (isNull response.Error) then
                for error in response.Error do
                    eprintf "Response warning: %s" error

    let ping (cl : Sc2Connection) = async {
        let request = new SC2APIProtocol.Request()
        request.Ping <- RequestPing()
        let! response = sendRequest cl request
        let pingResponse = response.Ping
        checkNullAndWarnings response pingResponse
        return pingResponse, response.Status }

    let connect address port timeout tok = async {
        let mailbox =
            Agent.Start(fun mailbox -> async {
                let mutable recover = ignore
                try
                    use cl = new ClientWebSocket()
                    let fullAddress = System.Uri (sprintf "ws://%s:%d/sc2api" address port)
                    let! connected = cl.ConnectAsync(fullAddress, tok) |> Async.AwaitTaskWithoutAggregate
                    let mutable stayConnected = true
                    let receiveBuf = System.ArraySegment(Array.zeroCreate (1024*1024))
                    let sendBuf = System.ArraySegment(Array.zeroCreate (1024*1024))

                    let writeMessage (req:Request) = async {
                        use co = new Google.Protobuf.CodedOutputStream(sendBuf.Array)
                        req.WriteTo(co)
                        let written = int co.Position
                        let send = System.ArraySegment(sendBuf.Array, 0, written)
                        do! cl.SendAsync(send, WebSocketMessageType.Binary, true, tok) |> Async.AwaitTaskWithoutAggregate }
                    let readMessage () = async {
                        let mutable finished = false
                        let mutable curPos = 0
                        while not finished do
                            let left = sendBuf.Array.Length - curPos
                            if left <= 0 then
                                failwithf "Our buffer wasn't large enough for the current message!"
                            let segment = System.ArraySegment(receiveBuf.Array, curPos, left)
                            let! result = cl.ReceiveAsync(segment, tok) |> Async.AwaitTaskWithoutAggregate
                            match result.MessageType with
                            | WebSocketMessageType.Binary ->
                                curPos <- curPos + result.Count
                                finished <- result.EndOfMessage
                            | _ ->
                                failwithf "Expected a binary response!"
                        
                                
                        let response = Response.Parser.ParseFrom(new System.IO.MemoryStream(receiveBuf.Array, 0, curPos))
                        return response }


                    while stayConnected do
                        let! request = mailbox.Receive()
                        match request with
                        | SendRequest (req, reply) ->
                            recover <- Error >> reply.Reply
                            do! writeMessage req
                            let! resp = readMessage()
                            recover <- ignore
                            reply.Reply(Success resp)
                        | Disconnect (sendQuit, reply) ->
                            recover <- Some >> reply.Reply
                            stayConnected <- false
                            if sendQuit then
                                // Cleanup
                                let quit = new Request()
                                quit.Quit <- new RequestQuit()
                                do! writeMessage quit
                            recover <- ignore
                            reply.Reply(None)

                with e ->
                    // "recover" from a failed request
                    let catch = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e)
                    recover catch

                    // respond to future requests
                    while true do
                        let! request = mailbox.Receive()
                        match request with
                        | SendRequest (req, reply) -> reply.Reply(ClientResponse.Error catch)
                        | Disconnect (_, reply) -> reply.Reply(Some catch)
                
                // Notify everyone that we are disconnected.
                let catch = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(new ClientDisconnectedException("Client was already disconnected", null))
                while true do
                    let! request = mailbox.Receive()
                    match request with
                    | SendRequest (req, reply) -> reply.Reply(ClientResponse.Error catch)
                    | Disconnect (_, reply) -> reply.Reply(None)                
            })

        let con = { Client = mailbox; _Address = address; _Port = port; _Timeout = timeout }
        let! _ = ping con
        return con }

    let inline checkError (error:'T when 'T : enum<int>) (errorDetails:string) =
        if error :> obj :?> int <> 0 then
            raise <| TypedResponseErrorException<'T>(error, errorDetails)

    let createGame (createGame: RequestCreateGame) (cl : Sc2Connection) : Async<Status> = async {
        let request = new SC2APIProtocol.Request()
        request.CreateGame <- createGame
        let! response = sendRequest cl request
        let createGameResponse = response.CreateGame
        checkNullAndWarnings response createGameResponse
        checkError createGameResponse.Error createGameResponse.ErrorDetails
        return response.Status }

    let joinGame (joinGame: RequestJoinGame) (cl : Sc2Connection) : Async<PlayerId * Status> = async {
        let request = new SC2APIProtocol.Request()
        request.JoinGame <- joinGame
        let! response = sendRequest cl request
        let joinGameResponse = response.JoinGame
        checkNullAndWarnings response joinGameResponse
        checkError joinGameResponse.Error joinGameResponse.ErrorDetails
        return joinGameResponse.PlayerId, response.Status }

    let getGameInfo (cl : Sc2Connection) = async {
        let request = new SC2APIProtocol.Request()
        request.GameInfo <- new RequestGameInfo()
        let! response = sendRequest cl request
        let gameInfoResponse = response.GameInfo
        checkNullAndWarnings response gameInfoResponse
        return gameInfoResponse, response.Status }

    let getObservation disableFog (cl : Sc2Connection) = async {
        let request = new SC2APIProtocol.Request()
        request.Observation <- new RequestObservation()
        request.Observation.DisableFog <- disableFog
        let! response = sendRequest cl request
        let observationResponse = response.Observation
        checkNullAndWarnings response observationResponse
        return observationResponse, response.Status }

    let doStep stepSize (cl : Sc2Connection) = async {
        let request = new SC2APIProtocol.Request()
        request.Step <- new RequestStep()
        request.Step.Count <- stepSize
        let! response = sendRequest cl request
        let stepResponse = response.Step
        checkNullAndWarnings response stepResponse
        return response.Status }

    let doActions actions (cl : Sc2Connection) = async {
        let request = new SC2APIProtocol.Request()
        request.Action <- new RequestAction()
        for action in actions do
            request.Action.Actions.Add(action:Action)
        let! response = sendRequest cl request
        let actionResponse = response.Action
        checkNullAndWarnings response actionResponse
        return actionResponse.Result, response.Status }