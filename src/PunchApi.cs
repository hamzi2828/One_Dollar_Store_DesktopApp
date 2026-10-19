using System;
using System.Collections.Generic;

namespace DollarCoAttendance
{
    // The two ways a fingerprint punch goes to POST /api/desktop/punch, in one
    // place (the counter window and the self-test both use these).
    public static class PunchApi
    {
        public const int LiveTimeoutMs = 12000;
        public const int QueuedTimeoutMs = 20000;

        // A punch right now: the server uses its own clock.
        public static Dictionary<string, object> SendLive(ApiClient api, string employeeId, string clientId, string deviceName)
        {
            var body = new Dictionary<string, object>();
            body["employeeId"] = employeeId;
            body["method"] = "fingerprint";
            body["clientId"] = clientId;
            body["deviceName"] = deviceName;
            return api.Post("api/desktop/punch", body, LiveTimeoutMs);
        }

        // A punch saved offline, sent later under the SAME clientId. `at` and
        // `sentAt` are both on this PC's clock: the server measures the punch's
        // age from the two, so a PC whose clock is wrong still files the right
        // time. Punches older than 48 h are refused (a manager adds them).
        public static Dictionary<string, object> SendQueued(ApiClient api, QueuedPunch punch, string deviceName)
        {
            var body = new Dictionary<string, object>();
            body["employeeId"] = punch.EmployeeId;
            body["method"] = "fingerprint";
            body["clientId"] = punch.ClientId;
            body["deviceName"] = deviceName;
            body["offline"] = true;
            body["at"] = ShopTime.ToIso(punch.ScannedAtUtc);
            body["sentAt"] = ShopTime.ToIso(DateTime.UtcNow);
            return api.Post("api/desktop/punch", body, QueuedTimeoutMs);
        }

        // What to do with a queued punch after a send: null = keep it and try
        // later (no answer, or the server is still busy with it); true = done;
        // false = the server answered and will not record it.
        public static bool? QueuedOutcome(Dictionary<string, object> result)
        {
            if (Json.IsOutage(result) || Json.IsRejection(result)) return null;
            if (Json.HttpStatus(result) == 409 && Json.Code(result) == "PUNCH_IN_PROGRESS") return null;
            return Json.Ok(result);
        }

        public static string NewClientId()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
