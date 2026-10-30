-- More kinds of business can sign up (spa and massage, nail and lash studio, pet grooming and vet, fitness, tattoo and
-- piercing, car wash and auto care, tutoring and lessons, photo studio). The list of allowed industries is a CHECK
-- constraint, so it is replaced by a wider one; every existing row already satisfies it (barbershop, salon, clinic stay).

ALTER TABLE business DROP CONSTRAINT business_industry;
ALTER TABLE business ADD CONSTRAINT business_industry CHECK (industry IN
    ('barbershop', 'salon', 'clinic', 'spa', 'nails', 'pet', 'fitness', 'tattoo', 'auto', 'tutor', 'photo'));
