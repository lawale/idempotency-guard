-- KEYS[1] = idempotency:{key}
-- ARGV[1] = response JSON
-- ARGV[2] = response_ttl_ms

local existing = redis.call('GET', KEYS[1])
if not existing then
    return 0
end

redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
return 1
