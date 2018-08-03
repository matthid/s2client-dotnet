namespace Starcraft2

[<AutoOpen>]
module internal Result =

    // apply either a success function or failure function
    let either successFunc failureFunc twoTrackInput =
        match twoTrackInput with
        | Ok s -> successFunc s
        | Error f -> failureFunc f

    // convert a switch function into a two-track function
    //let bind f = 
    //    either f fail

    // convert a one-track function into a switch
    let switch f = 
        f >> Ok

    // convert a one-track function into a two-track function
    //let map f = 
    //    either (f >> succeed) fail

    // convert a dead-end function into a one-track function
    let tee f x = 
        f x; x 

    // convert a one-track function into a switch with exception handling
    let tryCatch f exnHandler x =
        try
            f x |> Ok
        with
        | ex -> exnHandler ex |> Error

    // convert two one-track functions into a two-track function
    let doubleMap successFunc failureFunc =
        either (successFunc >> Ok) (failureFunc >> Error)

    // add two switches in parallel
    let plus addSuccess addFailure switch1 switch2 x = 
        match (switch1 x),(switch2 x) with
        | Ok s1, Ok s2 -> Ok (addSuccess s1 s2)
        | Error f1, Ok _  -> Error f1
        | Ok _ , Error f2 -> Error f2
        | Error f1, Error f2 -> Error (addFailure f1 f2)

    let bindAsyncInput binder asyncInput = async{
            let! input = asyncInput
            return Result.bind binder input
        }

    let eitherAsync successFunc failureFunc asyncInput = async{
        let! input = asyncInput
        return either successFunc failureFunc input
    }
        

    let bindAsyncBinder asyncBinder input = async{
        match input with
        |Error er -> return Error er
        |Ok inp -> return! asyncBinder inp
    }

    let bindAsync asyncBinder asyncInput = async {
        let! input = asyncInput
        return! bindAsyncBinder asyncBinder input
    }

    let mapAsyncInput f asyncInput = async {
        let! input = asyncInput
        return Result.map f input
    }

    let mapAsyncMapper asyncMapper input = async {
        match input with
        |Error er -> return Error er
        |Ok inp -> return! asyncMapper inp
    }

    let mapAsync asyncMapper asyncInput = async {
        let! input = asyncInput
        return! mapAsyncMapper asyncMapper input
    }

    let listFold resultList =
        resultList
        |> List.fold (fun resultState resultElem ->
            match resultState, resultElem with
            |Ok state, Ok elem -> elem::state |> Ok
            |Error er, _ -> Error er
            |_, Error er -> Error er
        ) (Ok [])

[<AutoOpen>]
module RailOps =
    // pipe a two-track value into a switch function
    let (>>=) x f = 
        Result.bind f x

    // compose two switches into another switch
    let (>=>) s1 s2 = 
        s1 >> Result.bind s2