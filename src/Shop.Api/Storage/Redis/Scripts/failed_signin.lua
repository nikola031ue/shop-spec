-- One failed admin sign-in, applied atomically to the (account, address) scope and the shop-wide
-- scope. Mirrors LockoutState.ApplyFailure and LockoutPolicy.ApplyFailure (Domain/Admin.cs).
-- KEYS[1] shop:admin:lock:<account>:<address>  KEYS[2] shop:admin:lock:*
-- ARGV[1] now (ms)
-- ARGV[2..5] scope:      window (ms), max failures, lock duration (ms), retention (ms)
-- ARGV[6..9] shop-wide:  window (ms), max failures, lock duration (ms), retention (ms)
local now = tonumber(ARGV[1])

local function locked(key)
  local untilMs = tonumber(redis.call('HGET', key, 'lockedUntil') or '')
  return untilMs ~= nil and untilMs > now
end

-- An attempt while either scope is locked counts nowhere (LockoutState.ApplyFailure): concurrent
-- requests from one locked address must not add up to the shop-wide ceiling.
if locked(KEYS[1]) or locked(KEYS[2]) then
  return 'locked'
end

local function apply(key, window, max, lockfor, retention)
  local start = tonumber(redis.call('HGET', key, 'failedWindowStart') or '')
  local count = tonumber(redis.call('HGET', key, 'failedCount') or '') or 0
  if (not start) or (now - start >= window) then
    start = now
    count = 1
  else
    count = count + 1
  end
  if count >= max then
    redis.call('HSET', key, 'failedCount', '0', 'failedWindowStart', '',
               'lockedUntil', string.format('%d', now + lockfor))
  else
    redis.call('HSET', key, 'failedCount', tostring(count),
               'failedWindowStart', string.format('%d', start), 'lockedUntil', '')
  end
  -- The key expires once its state no longer matters (15 min after the last change).
  redis.call('PEXPIRE', key, retention)
end

apply(KEYS[1], tonumber(ARGV[2]), tonumber(ARGV[3]), tonumber(ARGV[4]), tonumber(ARGV[5]))
apply(KEYS[2], tonumber(ARGV[6]), tonumber(ARGV[7]), tonumber(ARGV[8]), tonumber(ARGV[9]))
return 'ok'
