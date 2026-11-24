-- Increment the admin session version, optionally setting a new password in the same step.
-- KEYS[1] shop:admin
-- ARGV (optional) [1] passwordHash  [2] changedAt (ms)
if redis.call('EXISTS', KEYS[1]) == 0 then
  return redis.error_reply('admin record missing')
end
if redis.call('HEXISTS', KEYS[1], 'sessionVersion') == 0 then
  redis.call('HSET', KEYS[1], 'sessionVersion', '1')
end
if #ARGV == 2 then
  redis.call('HSET', KEYS[1], 'passwordHash', ARGV[1], 'initial', '0', 'changedAt', ARGV[2])
end
return redis.call('HINCRBY', KEYS[1], 'sessionVersion', 1)
