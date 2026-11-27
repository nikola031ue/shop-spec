-- Moves an address's chain progress forward, never backwards (spec 005 data-model "Chain progress").
-- KEYS[1] shop:chain-progress  KEYS[2] shop:addresses
-- ARGV[1] address  ARGV[2] block  ARGV[3] time (ms)
local current = redis.call('HGET', KEYS[1], ARGV[1])
local currentBlock = current and tonumber(string.match(current, '^(%d+)|'))
if not currentBlock or currentBlock < tonumber(ARGV[2]) then
  redis.call('HSET', KEYS[1], ARGV[1], ARGV[2] .. '|' .. ARGV[3])
end
redis.call('SADD', KEYS[2], ARGV[1])
return 1
