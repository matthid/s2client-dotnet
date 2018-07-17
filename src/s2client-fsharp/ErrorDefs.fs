namespace Starcraft2

type ApplicationError =
    |FailedToEstablishConnection of string
    |SendMessageBufferTooSmall
    |ExpectedBinaryResponse
    |FailedToSendMessage of string
    |FailedToReceiveMessage of string
    |NullResultWithNoError
    |NullResultWithError of string seq
    |ExecutableNotFound of string
    |ConfigError of string
    |GameNotStarted
    |GameNotJoined
    |NotInGame
    |BotError