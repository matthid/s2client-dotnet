namespace Starcraft2

type ApplicationError =
    |FailedToEstablishConnection of exn
    |SendMessageBufferTooSmall
    |ExpectedBinaryResponse
    |FailedToSendMessage of exn
    |FailedToReceiveMessage of exn
    |NullResultWithNoError
    |NullResultWithError of string seq
    |ExecutableNotFound of string
    |ConfigError of string
    |GameNotStarted
    |GameNotJoined
    |NotInGame
    |BotError