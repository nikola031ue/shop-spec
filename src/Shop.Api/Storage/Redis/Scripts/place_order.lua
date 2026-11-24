-- Atomic order placement (research R9): check every line, then decrement stock and store the
-- order, or change nothing and return the problems.
-- KEYS[1] shop:items  KEYS[2] shop:order:{reference}  KEYS[3] shop:orders
-- ARGV[1] reference  ARGV[2] createdAt (ms)  ARGV[3] walletAddress  ARGV[4] status
-- ARGV[5..] itemId, quantity, expectedUnitPriceCents
-- The expected price is only compared (FR-013). Stored line prices and the total are always
-- taken from the stored item, never from the request.
if redis.call('EXISTS', KEYS[2]) == 1 then
  return cjson.encode({taken = true})
end

local found, problems, lines = {}, {}, {}
local total = 0
for i = 5, #ARGV, 3 do
  local id, qty, expected = ARGV[i], tonumber(ARGV[i + 1]), tonumber(ARGV[i + 2])
  local raw = redis.call('HGET', KEYS[1], id)
  if not raw then
    problems[#problems + 1] = {itemId = id, kind = 'removed'}
  else
    local item = cjson.decode(raw)
    if item.priceCents ~= expected then
      problems[#problems + 1] = {itemId = id, kind = 'price', currentPriceCents = item.priceCents,
                                 currentStock = item.stock}
    elseif item.stock < qty then
      problems[#problems + 1] = {itemId = id, kind = 'stock', currentStock = item.stock}
    else
      found[#found + 1] = {id = id, item = item, qty = qty}
      lines[#lines + 1] = {itemId = id, name = item.name, unitPriceCents = item.priceCents, quantity = qty}
      -- Exact: the app rejects orders whose total exceeds 2^53 - 1 (OrderRules.MaxTotalCents).
      total = total + item.priceCents * qty
    end
  end
end

if #problems > 0 then
  return cjson.encode({problems = problems})
end

local createdAt = tonumber(ARGV[2])
for _, e in ipairs(found) do
  e.item.stock = e.item.stock - e.qty
  e.item.updatedAt = createdAt
  redis.call('HSET', KEYS[1], e.id, cjson.encode(e.item))
end

-- The total is written with %.0f because cjson keeps only 14 significant digits.
local order = '{"reference":' .. cjson.encode(ARGV[1]) .. ',"createdAt":' .. ARGV[2] ..
  ',"status":' .. cjson.encode(ARGV[4]) .. ',"walletAddress":' .. cjson.encode(ARGV[3]) ..
  ',"totalCents":' .. string.format('%.0f', total) .. ',"lines":' .. cjson.encode(lines) .. '}'
redis.call('SET', KEYS[2], order)
redis.call('ZADD', KEYS[3], ARGV[2], ARGV[1])
return order
