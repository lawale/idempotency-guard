-- KEYS[1] = idempotency:{key}
-- ARGV[1] = response JSON
-- ARGV[2] = response_ttl_ms

local existing = redis.call('GET', KEYS[1])
if not existing then
    return 0
end

-- Preserve the original fingerprint from the claim entry
local claimed = cjson.decode(existing)
local completed = cjson.decode(ARGV[1])
completed.fingerprint = claimed.fingerprint
local merged = cjson.encode(completed)

redis.call('SET', KEYS[1], merged, 'PX', ARGV[2])
return 1
