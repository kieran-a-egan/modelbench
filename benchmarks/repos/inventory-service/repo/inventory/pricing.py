TAX_RATE = 0.2


def with_tax(amount):
    return amount * (1 + TAX_RATE)


def total_price(unit_price, qty):
    return round(with_tax(unit_price * qty), 2)
