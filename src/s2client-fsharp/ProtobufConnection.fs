namespace Starcraft2
open System
//open SC2APIProtocol
open Rail

module ProtobufConnection =
    open System.Net.WebSockets

    type private ClientRequest =
        |SendRequest of SC2APIProtocol.Request * AsyncReplyChannel<Result<SC2APIProtocol.Response, ApplicationError>>

    type Sc2Connection(address:string, port:int, timeout:TimeSpan, tok) =
        let connectedSocket =
            let watch = System.Diagnostics.Stopwatch.StartNew()
            let rec getConnectedSocket() =
                try
                    let clientSock = new ClientWebSocket()
                    let fullAddress = System.Uri (sprintf "ws://%s:%d/sc2api" address port)
                    clientSock.ConnectAsync(fullAddress, tok) |> Async.AwaitTask |> Async.RunSynchronously
                    clientSock |> Ok
                with
                |_ when watch.Elapsed < timeout ->
                    getConnectedSocket()
                |ex -> ex.Message |> FailedToEstablishConnection |> Error
            getConnectedSocket()

        let receiveBuf = System.ArraySegment(Array.zeroCreate (1024*1024))
        let sendBuf = System.ArraySegment(Array.zeroCreate (1024*1024))

        let writeMessage (client:ClientWebSocket) (req:SC2APIProtocol.Request) = async {
            try
                use co = new Google.Protobuf.CodedOutputStream(sendBuf.Array)
                req.WriteTo(co)
                let written = int co.Position
                let send = System.ArraySegment(sendBuf.Array, 0, written)
                do! client.SendAsync(send, WebSocketMessageType.Binary, true, tok) |> Async.AwaitTask
                return Ok ()
            with
            |ex -> return ex.Message |> FailedToSendMessage |> Error
        }

        let readMessage (client:ClientWebSocket) = async {
            let rec innerLoop curPos = async {
                let left = sendBuf.Array.Length - curPos
                if left <= 0 then
                    return Error SendMessageBufferTooSmall
                else
                    try
                        let segment = System.ArraySegment(receiveBuf.Array, curPos, left)
                        let! result = client.ReceiveAsync(segment, tok) |> Async.AwaitTask
                        match result.MessageType, result.EndOfMessage with
                        |WebSocketMessageType.Binary, false ->
                            return! innerLoop (curPos + result.Count)
                        |WebSocketMessageType.Binary, true -> return Ok (curPos + result.Count)
                        |_ -> return Error ExpectedBinaryResponse
                    with
                    |ex -> return ex.Message |> FailedToReceiveMessage |> Error
            }

            let parseFrom finalPos =
                SC2APIProtocol.Response.Parser.ParseFrom(new System.IO.MemoryStream(receiveBuf.Array, 0, finalPos))

            return!
                innerLoop 0
                |> Result.mapAsyncInput parseFrom
        }

        let getAgent client = 
            let getResponse() =
                readMessage client
            MailboxProcessor.Start (fun inbox ->
                let rec messageLoop() = async{
                    let! msg = inbox.Receive()

                    match msg with
                    |SendRequest (req, replyChannel) ->
                        let! resp =
                            writeMessage client req
                            |> Result.bindAsync getResponse

                        replyChannel.Reply resp

                    return! messageLoop()
                }
                messageLoop()
            )

        let postSendRequest req (agent:MailboxProcessor<ClientRequest>) =
            agent.PostAndAsyncReply (fun reply -> SendRequest (req, reply))

        let sendRequest req = async{
            return!
                connectedSocket
                |> Result.map getAgent
                |> Result.bindAsyncBinder (postSendRequest req)
        }

        member this.SendRequest = sendRequest

    let connect address port timeout tok = async{
        return Sc2Connection(address, port, timeout, tok)
    }

    let private applyFieldCheckAndReturnFunction fieldCheck returnFunc (response:SC2APIProtocol.Response)  =
        match fieldCheck response, response.Error with
        |null, null ->
            Error NullResultWithNoError
        |null, _ ->
            Error (NullResultWithError response.Error)
        |_, sq when not (isNull sq) ->
            sq |> Seq.iter (fun s -> eprintfn "Response warning: %s" s)
            response |> returnFunc |> Ok
        |_, _ -> 
            response |> returnFunc |> Ok

    //let inline checkError (error:'T when 'T : enum<int>) (errorDetails:string) =
    //    if error :> obj :?> int <> 0 then
    //        raise <| TypedResponseErrorException<'T>(error, errorDetails)

    let private genericInteractionFunction applyRequestField getResponseField getResult  (client:Sc2Connection) = async {
        let request = SC2APIProtocol.Request() |> applyRequestField
        let! responseResult = client.SendRequest request
        return
            responseResult
            |> Result.bind (applyFieldCheckAndReturnFunction getResponseField getResult)
        }

    let createGame createGameReq client =
        genericInteractionFunction 
            (fun (req:SC2APIProtocol.Request) -> req.CreateGame <- createGameReq; req)
            (fun (resp:SC2APIProtocol.Response) -> resp.CreateGame)
            (fun (resp:SC2APIProtocol.Response) -> (), resp.Status)
            client

    let joinGame joinGameReq client =
        genericInteractionFunction 
            (fun (req:SC2APIProtocol.Request) -> req.JoinGame <- joinGameReq; req)
            (fun (resp:SC2APIProtocol.Response) -> resp.JoinGame)
            (fun (resp:SC2APIProtocol.Response) -> resp.JoinGame.PlayerId, resp.Status)
            client

    let getGameInfo client =
        genericInteractionFunction 
            (fun (req:SC2APIProtocol.Request) -> req.GameInfo <- SC2APIProtocol.RequestGameInfo(); req)
            (fun (resp:SC2APIProtocol.Response) -> resp.GameInfo)
            (fun (resp:SC2APIProtocol.Response) -> resp.GameInfo, resp.Status)
            client

    let getObservation disableFog client =
        genericInteractionFunction 
            (fun (req:SC2APIProtocol.Request) -> req.Observation <- SC2APIProtocol.RequestObservation(); req.Observation.DisableFog <- disableFog; req)
            (fun (resp:SC2APIProtocol.Response) -> resp.Observation)
            (fun (resp:SC2APIProtocol.Response) -> resp.Observation, resp.Status)
            client

    let doStep stepSize client = 
        genericInteractionFunction 
            (fun (req:SC2APIProtocol.Request) -> req.Step <- SC2APIProtocol.RequestStep(); req.Step.Count <- stepSize; req)
            (fun (resp:SC2APIProtocol.Response) -> resp.Observation)
            (fun (resp:SC2APIProtocol.Response) -> (), resp.Status)
            client

    let doActions (actions:SC2APIProtocol.Action seq) client =
        genericInteractionFunction 
            (fun (req:SC2APIProtocol.Request) -> req.Action <- SC2APIProtocol.RequestAction(); actions |> Seq.iter (fun action -> req.Action.Actions.Add(action)); req)
            (fun (resp:SC2APIProtocol.Response) -> resp.Action)
            (fun (resp:SC2APIProtocol.Response) -> resp.Action.Result, resp.Status)
            client