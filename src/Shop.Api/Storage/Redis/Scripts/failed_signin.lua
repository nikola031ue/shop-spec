-- One failed admin sign-in, applied atomically. Mirrors LockoutPolicy.ApplyFailure (Domain/Admin.cs).
-- KEYS[1] shop:admin
-- ARGV[1] now (ms)  ARGV[2] window (ms)  ARGV[3] max failures  ARGV[4] lock duration (ms)
if redis.call('EXISTS', KEYS[1]) == 0 then
  return redis.error_reply('admin record missing')
end
local now = tonumber(ARGV[1])
local locked = tonumber(redis.call('HGET', KEYS[1], 'lockedUntil') or '')
if locked and locked > now then
  return 'locked'
end
local start = tonumber(redis.call('HGET', KEYS[1], 'failedWindowStart') or '')
local count = tonumber(redis.call('HGET', KEYS[1], 'failedCount') or '') or 0
if (not start) or (now - start >= tonumber(ARGV[2])) then
  start = now
  count = 1
else
  count = count + 1
end
if count >= tonumber(ARGV[3]) then
  redis.call('HSET', KEYS[1], 'failedCount', '0', 'failedWindowStart', '',
             'lockedUntil', string.format('%d', now + tonumber(ARGV[4])))
else
  redis.call('HSET', KEYS[1], 'failedCount', tostring(count),
             'failedWindowStart', string.format('%d', start))
end
return 'ok'
