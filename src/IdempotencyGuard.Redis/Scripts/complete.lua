-- @key = idempotency:{key}
-- @response = response JSON
-- @response_ttl_ms = response_ttl_ms

local existing = redis.call('GET', @key)
if not existing then
    return 0
end

-- Preserve the original fingerprint from the claim entry
local claimed = cjson.decode(existing)
local completed = cjson.decode(@response)
completed.fingerprint = claimed.fingerprint
local merged = cjson.encode(completed)

redis.call('SET', @key, merged, 'PX', @response_ttl_ms)
return 1
