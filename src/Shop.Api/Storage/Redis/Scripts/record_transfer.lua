-- Records one confirmed transfer atomically (spec 005 research R6): pays the matching order or
-- records it as not matched with a reason; idempotent on (tx, logIndex). Same rule as
-- PaymentMatching.Decide (C#); the shared store contract tests hold both to it.
-- Order keys are derived from stored references (single Redis instance, like every light shop).
-- ARGV[1] tx  ARGV[2] logIndex  ARGV[3] block  ARGV[4] blockTime (ms)  ARGV[5] from  ARGV[6] to
-- ARGV[7] value (decimal string, base units)  ARGV[8] receivedAt (ms)
-- Returns 'recorded' (already there), 'paid' or 'unmatched'.
local tx, logIndex, block, blockTime = ARGV[1], ARGV[2], ARGV[3], tonumber(ARGV[4])
local from, to, value, receivedAt = ARGV[5], ARGV[6], ARGV[7], ARGV[8]
local member = tx .. ':' .. logIndex
local paymentKey = 'shop:payment:' .. member
if redis.call('EXISTS', paymentKey) == 1 then
  return 'recorded'
end

-- An order with its current status (the state hash overrides the status written at placement)
-- and its total in base units as an exact decimal string (cents × 10^4).
local function load(reference)
  local raw = redis.call('GET', 'shop:order:' .. reference)
  if not raw then
    return nil
  end
  local o = cjson.decode(raw)
  o.status = redis.call('HGET', 'shop:order-state:' .. reference, 'status') or o.status
  o.deadline = o.deadline or (o.createdAt + 30 * 60 * 1000)
  o.baseUnits = string.format('%.0f', o.totalCents) .. '0000'
  if o.totalCents == 0 then
    o.baseUnits = '0'
  end
  return o
end

local paid, reason
local waitingRef = redis.call('GET', 'shop:waiting-account:' .. from)
local waiting = waitingRef and load(waitingRef)
if waiting then
  if waiting.walletAddress ~= to or blockTime < waiting.createdAt then
    reason = 'unknown'
  elseif waiting.baseUnits ~= value then
    reason = 'amount'
  elseif blockTime > waiting.deadline then
    reason = 'late'
  else
    paid = waiting
  end
else
  local latestRef = redis.call('GET', 'shop:account-last:' .. from .. ':' .. to)
  local latest = latestRef and load(latestRef)
  if not latest then
    reason = 'account'
  elseif latest.baseUnits ~= value then
    reason = 'unknown'
  elseif latest.status == 'Paid' then
    reason = 'duplicate'
  elseif latest.status == 'Expired' then
    reason = 'late'
  else
    reason = 'unknown'
  end
end

local payment = '{"tx":' .. cjson.encode(tx) .. ',"logIndex":' .. logIndex .. ',"block":' .. block ..
  ',"blockTime":' .. ARGV[4] .. ',"fromAccount":' .. cjson.encode(from) .. ',"toAddress":' .. cjson.encode(to) ..
  ',"value":' .. cjson.encode(value) .. ',"orderReference":' .. (paid and cjson.encode(paid.reference) or 'null') ..
  ',"unmatchedReason":' .. (reason and cjson.encode(reason) or 'null') .. ',"receivedAt":' .. receivedAt .. '}'
redis.call('SET', paymentKey, payment)
redis.call('ZADD', 'shop:payments', ARGV[4], member)

if not paid then
  redis.call('ZADD', 'shop:payments-unmatched', ARGV[4], member)
  return 'unmatched'
end

redis.call('HSET', 'shop:order-state:' .. paid.reference, 'status', 'Paid', 'paidTx', tx,
  'paidLogIndex', logIndex, 'paidBlock', block, 'paidAt', ARGV[4])
redis.call('DEL', 'shop:waiting-account:' .. from)
redis.call('ZREM', 'shop:waiting', paid.reference)
redis.call('SREM', 'shop:waiting-address:' .. paid.walletAddress, paid.reference)
return 'paid'
